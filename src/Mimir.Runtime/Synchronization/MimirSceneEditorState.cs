using System.Numerics;
using Aquarium.Engine;
using Aquarium.Engine.Input;
using Aquarium.Engine.Render;
using Aquarium.Engine.Ui;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirSceneEditorState
{
    private const float ProgramWorldWidth = 12.8f;
    private const float ProgramWorldHeight = 7.2f;
    private const float ProgramAspect = ProgramWorldWidth / ProgramWorldHeight;
    private const float MinProgramExtent = 0.01f;
    private const float ProgramSnapThreshold = 0.015f;
    private readonly List<MimirSceneEditorNode> nodes = [];
    private readonly HashSet<string> configuredProgramSources = new(StringComparer.Ordinal);
    private bool selectedConfiguredSource;
    private Vector2? previousMousePosition;

    public MimirSceneEditorState()
    {
        nodes.Add(new MimirSceneEditorNode(
            "editor-camera",
            "",
            "Editor Camera",
            MimirSceneEditorNodeKind.Camera,
            new MimirSceneEditorTransform(new Vector3(0.0f, 0.0f, -18.0f), 0.0f, Vector2.One)));
        SelectedNodeId = "editor-camera";
    }

    public bool Enabled { get; set; } = true;

    public MimirSceneEditorGizmoMode GizmoMode { get; set; } = MimirSceneEditorGizmoMode.Translate;

    public string SelectedNodeId { get; private set; }

    public IReadOnlyList<MimirSceneEditorNode> Nodes => nodes;

    public MimirSceneEditorNode? SelectedNode =>
        nodes.FirstOrDefault(node => string.Equals(node.Id, SelectedNodeId, StringComparison.Ordinal));

    public IReadOnlyList<MimirSceneEditorNode> VisibleNodes =>
        nodes.Where(node => node.Visible).ToArray();

    public IReadOnlyList<AquariumUiPreviewItem> PreviewItems =>
        nodes
            .Where(static node => node.Visible && node.Kind == MimirSceneEditorNodeKind.SensorFeedPanel)
            .OrderBy(static node => node.Layer)
            .Select(node =>
            {
                var placement = PlacementForNode(node);
                return new AquariumUiPreviewItem(
                    node.Id,
                    node.DisplayName,
                    placement.CenterX - placement.Width * 0.5f,
                    placement.CenterY - placement.Height * 0.5f,
                    placement.Width,
                    placement.Height,
                    string.Equals(node.Id, SelectedNodeId, StringComparison.Ordinal),
                    "cool");
            })
            .ToArray();

    public Vector3 CameraPosition => nodes
        .First(node => node.Kind == MimirSceneEditorNodeKind.Camera)
        .Transform.Position;

    public Vector3 CameraTarget { get; private set; } = Vector3.Zero;

    public AquariumCameraFrustum CameraFrustum { get; private set; } =
        new(-0.85f, 0.85f, -0.48f, 0.48f, 0.1f, 120.0f);

    public void SyncSensorFeeds(IEnumerable<MimirRollingStreamBuffer> buffers)
    {
        var feeds = buffers
            .Where(static buffer => buffer.Descriptor.Kind == MimirStreamKind.Video)
            .OrderBy(static buffer => buffer.Descriptor.SourceId, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < feeds.Length; index++)
        {
            var buffer = feeds[index];
            var id = FeedNodeId(buffer.Descriptor.SourceId);
            if (nodes.Any(node => string.Equals(node.Id, id, StringComparison.Ordinal)))
            {
                continue;
            }

            var row = index / 3;
            var column = index % 3;
            nodes.Add(new MimirSceneEditorNode(
                id,
                "feeds",
                buffer.Descriptor.Label,
                MimirSceneEditorNodeKind.SensorFeedPanel,
                new MimirSceneEditorTransform(
                    new Vector3(-3.6f + column * 3.6f, 0.9f - row * 2.2f, 0.0f),
                    0.0f,
                    new Vector2(2.8f, 1.6f)))
            {
                Layer = index,
                SourceId = buffer.Descriptor.SourceId,
                ProgramAspectRatio = ProgramAspectRatioFor(buffer),
            });
        }

        if (SelectedNode is not { Kind: MimirSceneEditorNodeKind.SensorFeedPanel } &&
            nodes.FirstOrDefault(static node => node.Kind == MimirSceneEditorNodeKind.SensorFeedPanel) is { } firstFeed)
        {
            SelectedNodeId = firstFeed.Id;
        }
    }

    public void ApplyProgramSurfaceConfig(MimirProgramSurfaceConfigDocument config)
    {
        foreach (var layer in config.Layers.OrderBy(static layer => layer.Layer))
        {
            if (configuredProgramSources.Contains(layer.SourceId) ||
                NodeForSource(layer.SourceId) is not { } node)
            {
                continue;
            }

            node.Visible = layer.Visible;
            node.Layer = Math.Max(0, layer.Layer);
            SetNodeProgramPlacement(node, new MimirCompositorPlacement(
                Math.Clamp(layer.CenterX, 0.0f, 1.0f),
                Math.Clamp(layer.CenterY, 0.0f, 1.0f),
                Math.Clamp(layer.Width, 0.01f, 1.0f),
                Math.Clamp(layer.Height, 0.01f, 1.0f),
                layer.RotationRadians,
                Math.Max(0, layer.Layer)));
            node.Locked = layer.Locked;
            configuredProgramSources.Add(layer.SourceId);
        }

        if (!selectedConfiguredSource &&
            !string.IsNullOrWhiteSpace(config.SelectedSourceId) &&
            NodeForSource(config.SelectedSourceId) is { } selected)
        {
            SelectedNodeId = selected.Id;
            selectedConfiguredSource = true;
        }
    }

    public void UpdateInput(float deltaSeconds, InputState input)
    {
        if (!Enabled)
        {
            previousMousePosition = null;
            return;
        }

        if (input.IsKeyPressed(KeyCode.Digit1))
        {
            GizmoMode = MimirSceneEditorGizmoMode.Translate;
        }
        else if (input.IsKeyPressed(KeyCode.Digit2))
        {
            GizmoMode = MimirSceneEditorGizmoMode.Rotate;
        }
        else if (input.IsKeyPressed(KeyCode.Digit3))
        {
            GizmoMode = MimirSceneEditorGizmoMode.Scale;
        }

        if (input.IsKeyPressed(KeyCode.LeftArrow))
        {
            SelectPrevious();
        }
        else if (input.IsKeyPressed(KeyCode.RightArrow))
        {
            SelectNext();
        }

        if (input.IsKeyPressed(KeyCode.Home))
        {
            ResetSelectedTransform();
        }

        if (input.IsKeyPressed(KeyCode.Delete))
        {
            RemoveSelectedMutableNode();
        }

        if (MathF.Abs(input.WheelDelta) > 0.0f)
        {
            var camera = nodes.First(node => node.Kind == MimirSceneEditorNodeKind.Camera);
            var cameraPosition = camera.Transform.Position;
            camera.Transform = camera.Transform with
            {
                Position = new Vector3(
                    cameraPosition.X,
                    cameraPosition.Y,
                    Math.Clamp(cameraPosition.Z + input.WheelDelta * 0.75f, -60.0f, -4.0f)),
            };
        }

        if (input.LeftMouseDown && previousMousePosition.HasValue && SelectedNode is { } selected && !selected.Locked)
        {
            var delta = input.MousePosition - previousMousePosition.Value;
            ApplyScreenDelta(selected, delta, Math.Max(deltaSeconds, 0.0001f));
        }

        previousMousePosition = input.LeftMouseDown
            ? input.MousePosition
            : null;
    }

    public void SelectNode(string nodeId)
    {
        if (nodes.Any(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal)))
        {
            SelectedNodeId = nodeId;
        }
    }

    public void SelectSource(string sourceId)
    {
        if (NodeForSource(sourceId) is { } node)
        {
            SelectedNodeId = node.Id;
        }
    }

    public void SelectNext() => SelectRelative(1);

    public void SelectPrevious() => SelectRelative(-1);

    public void SetSelectedVisible(bool visible)
    {
        if (SelectedNode is { } selected)
        {
            selected.Visible = visible;
        }
    }

    public void SetSelectedLocked(bool locked)
    {
        if (SelectedNode is { } selected)
        {
            selected.Locked = locked;
        }
    }

    public void SetSelectedX(float value) => SetSelectedPosition(axis: 0, value);

    public void SetSelectedY(float value) => SetSelectedPosition(axis: 1, value);

    public void SetSelectedZ(float value) => SetSelectedPosition(axis: 2, value);

    public void SetSelectedRotation(float value)
    {
        if (SelectedNode is { } selected && !selected.Locked)
        {
            selected.Transform = selected.Transform with { RotationRadians = value };
        }
    }

    public void SetSelectedScaleX(float value)
    {
        if (SelectedNode is { } selected && !selected.Locked)
        {
            selected.Transform = selected.Transform with
            {
                Scale = selected.Transform.Scale with { X = Math.Clamp(value, 0.05f, 8.0f) },
            };
        }
    }

    public void SetSelectedScaleY(float value)
    {
        if (SelectedNode is { } selected && !selected.Locked)
        {
            selected.Transform = selected.Transform with
            {
                Scale = selected.Transform.Scale with { Y = Math.Clamp(value, 0.05f, 8.0f) },
            };
        }
    }

    public float SelectedProgramX => SelectedNode is { } selected ? PlacementForNode(selected).CenterX : 0.5f;

    public float SelectedProgramY => SelectedNode is { } selected ? PlacementForNode(selected).CenterY : 0.5f;

    public float SelectedProgramWidth => SelectedNode is { } selected ? PlacementForNode(selected).Width : 0.1f;

    public float SelectedProgramHeight => SelectedNode is { } selected ? PlacementForNode(selected).Height : 0.1f;

    public void SetSelectedProgramX(float value)
    {
        if (SelectedNode is { } selected)
        {
            SetNodeProgramPlacement(selected, PlacementForNode(selected) with { CenterX = value });
        }
    }

    public void SetSelectedProgramY(float value)
    {
        if (SelectedNode is { } selected)
        {
            SetNodeProgramPlacement(selected, PlacementForNode(selected) with { CenterY = value });
        }
    }

    public void SetSelectedProgramWidth(float value)
    {
        if (SelectedNode is { } selected)
        {
            SetNodeProgramPlacement(selected, ResizeProgramPlacement(selected, PlacementForNode(selected), value, resizeWidth: true));
        }
    }

    public void SetSelectedProgramHeight(float value)
    {
        if (SelectedNode is { } selected)
        {
            SetNodeProgramPlacement(selected, ResizeProgramPlacement(selected, PlacementForNode(selected), value, resizeWidth: false));
        }
    }

    public void CenterSelectedProgramLayer()
    {
        if (SelectedNode is { } selected)
        {
            SetNodeProgramPlacement(selected, PlacementForNode(selected) with { CenterX = 0.5f, CenterY = 0.5f });
        }
    }

    public void FitSelectedProgramLayer()
    {
        if (SelectedNode is not { } selected)
        {
            return;
        }

        var placement = PlacementForNode(selected);
        var aspect = ProgramPlacementAspectFor(selected);
        var width = 1.0f;
        var height = 1.0f;
        if (aspect > 1.0f)
        {
            height = 1.0f / aspect;
        }
        else
        {
            width = aspect;
        }

        SetNodeProgramPlacement(selected, placement with
        {
            CenterX = 0.5f,
            CenterY = 0.5f,
            Width = width,
            Height = height,
        });
    }

    public void FillSelectedProgramLayer()
    {
        if (SelectedNode is { } selected)
        {
            SetNodeProgramPlacement(selected, PlacementForNode(selected) with
            {
                CenterX = 0.5f,
                CenterY = 0.5f,
                Width = ProgramPlacementAspectFor(selected) >= 1.0f ? 1.0f : ProgramPlacementAspectFor(selected),
                Height = ProgramPlacementAspectFor(selected) >= 1.0f ? 1.0f / ProgramPlacementAspectFor(selected) : 1.0f,
            });
        }
    }

    public void MoveSelectedLayer(int delta)
    {
        if (SelectedNode is not { Kind: MimirSceneEditorNodeKind.SensorFeedPanel } selected)
        {
            return;
        }

        var videoNodes = nodes
            .Where(static node => node.Kind == MimirSceneEditorNodeKind.SensorFeedPanel)
            .OrderBy(static node => node.Layer)
            .ThenBy(static node => node.SourceId, StringComparer.Ordinal)
            .ToList();
        var current = videoNodes.FindIndex(node => string.Equals(node.Id, selected.Id, StringComparison.Ordinal));
        if (current < 0)
        {
            return;
        }

        var next = Math.Clamp(current + delta, 0, videoNodes.Count - 1);
        if (next == current)
        {
            return;
        }

        videoNodes.RemoveAt(current);
        videoNodes.Insert(next, selected);
        for (var index = 0; index < videoNodes.Count; index++)
        {
            videoNodes[index].Layer = index;
        }
    }

    public void HandlePreviewInteraction(AquariumUiPreviewInteraction interaction)
    {
        if (nodes.FirstOrDefault(node => string.Equals(node.Id, interaction.ItemId, StringComparison.Ordinal)) is not { Kind: MimirSceneEditorNodeKind.SensorFeedPanel } node ||
            node.Locked)
        {
            return;
        }

        SelectedNodeId = node.Id;
        if (interaction.Phase != "drag")
        {
            return;
        }

        var placement = PlacementForNode(node);
        if (interaction.Handle == "move")
        {
            SetNodeProgramPlacement(node, placement with
            {
                CenterX = placement.CenterX + interaction.DeltaX,
                CenterY = placement.CenterY + interaction.DeltaY,
            });
            return;
        }

        SetNodeProgramPlacement(node, ResizeProgramPlacementFromHandle(node, placement, interaction.Handle, interaction.DeltaX, interaction.DeltaY));
    }

    public bool IncludesVideoSource(string sourceId) =>
        NodeForSource(sourceId) is not { } node || node.Visible;

    public MimirCompositorPlacement? PlacementForSource(string sourceId) =>
        NodeForSource(sourceId) is { } node ? PlacementForNode(node) : null;

    public void ResetSelectedTransform()
    {
        if (SelectedNode is { } selected && !selected.Locked)
        {
            selected.Transform = selected.DefaultTransform;
        }
    }

    public void ResetCamera()
    {
        var camera = nodes.First(node => node.Kind == MimirSceneEditorNodeKind.Camera);
        camera.Transform = camera.DefaultTransform;
        CameraTarget = Vector3.Zero;
    }

    public string DescribeHierarchy()
    {
        var feeds = nodes
            .Where(static node => node.Kind == MimirSceneEditorNodeKind.SensorFeedPanel)
            .OrderBy(static node => node.Layer)
            .ThenBy(static node => node.SourceId, StringComparer.Ordinal)
            .ToArray();
        if (feeds.Length == 0)
        {
            return "No synced video streams yet.";
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Program Video Layers");
        foreach (var node in feeds)
        {
            AppendNode(builder, node, 1);
        }

        return builder.ToString().TrimEnd();
    }

    public string DescribeSelection()
    {
        if (SelectedNode is not { } selected)
        {
            return "no selection";
        }

        if (selected.Kind != MimirSceneEditorNodeKind.SensorFeedPanel)
        {
            return "no video source selected";
        }

        var placement = PlacementForNode(selected);
        return $"{selected.Layer + 1}. {selected.DisplayName} source={selected.SourceId} frame={placement.CenterX:0.000},{placement.CenterY:0.000} {placement.Width:0.000}x{placement.Height:0.000}";
    }

    public AquariumSplineFrame BuildEditorSplineFrame()
    {
        var splines = new List<AquariumSpline3D>();
        foreach (var node in nodes.Where(static node => node.Visible && node.Kind != MimirSceneEditorNodeKind.Camera))
        {
            AppendNodeFrame(splines, node, IsSelected(node));
        }

        if (SelectedNode is { Visible: true } selected && selected.Kind != MimirSceneEditorNodeKind.Camera)
        {
            AppendGizmo(splines, selected);
        }

        return splines.Count == 0
            ? AquariumSplineFrame.Empty
            : new AquariumSplineFrame { Splines = splines };
    }

    public IReadOnlyList<AquariumSdfObject> BuildEditorSdfObjects()
    {
        if (SelectedNode is not { Visible: true } selected || selected.Kind == MimirSceneEditorNodeKind.Camera)
        {
            return [];
        }

        var corners = Corners(selected.Transform);
        return corners
            .Select((corner, index) => new AquariumSdfObject(
                new Vector4(corner, 0.075f),
                new Vector4(corner, 0.0f),
                new Vector4(0.2f + index * 0.02f, 0.0f, 0.0f, 0.0f)))
            .ToArray();
    }

    public IReadOnlyList<AquariumSdfLight> BuildEditorSdfLights() =>
    [
        new AquariumSdfLight(new Vector4(-3.5f, 3.0f, -4.5f, 4.0f), new Vector4(1.0f, 1.0f, 1.0f, 4.0f)),
        new AquariumSdfLight(new Vector4(3.0f, -2.0f, -2.5f, 3.0f), new Vector4(0.2f, 0.7f, 1.0f, 2.5f)),
    ];

    private void AppendNode(System.Text.StringBuilder builder, MimirSceneEditorNode node, int depth)
    {
        builder
            .Append(' ', depth * 2)
            .Append(IsSelected(node) ? "* " : "- ")
            .Append(node.Visible ? "[eye] " : "[off] ")
            .Append(node.DisplayName)
            .Append(" layer=")
            .Append(node.Layer + 1)
            .AppendLine();
    }

    private void SelectRelative(int delta)
    {
        if (nodes.Count == 0)
        {
            return;
        }

        var index = Math.Max(0, nodes.FindIndex(node => string.Equals(node.Id, SelectedNodeId, StringComparison.Ordinal)));
        var next = (index + delta + nodes.Count) % nodes.Count;
        SelectedNodeId = nodes[next].Id;
    }

    private void RemoveSelectedMutableNode()
    {
        return;
    }

    private void SetSelectedPosition(int axis, float value)
    {
        if (SelectedNode is not { } selected || selected.Locked)
        {
            return;
        }

        var position = selected.Transform.Position;
        position = axis switch
        {
            0 => position with { X = value },
            1 => position with { Y = value },
            2 => position with { Z = value },
            _ => position,
        };
        selected.Transform = selected.Transform with { Position = position };
    }

    private void ApplyScreenDelta(MimirSceneEditorNode selected, Vector2 screenDelta, float deltaSeconds)
    {
        var worldDelta = new Vector2(screenDelta.X, -screenDelta.Y) * (ProgramWorldWidth / 960.0f);
        switch (GizmoMode)
        {
            case MimirSceneEditorGizmoMode.Translate:
                var placement = PlacementForNode(selected);
                SetNodeProgramPlacement(selected, placement with
                {
                    CenterX = placement.CenterX + worldDelta.X / ProgramWorldWidth,
                    CenterY = placement.CenterY - worldDelta.Y / ProgramWorldHeight,
                });
                break;
            case MimirSceneEditorGizmoMode.Rotate:
                selected.Transform = selected.Transform with
                {
                    RotationRadians = selected.Transform.RotationRadians + screenDelta.X * 0.012f,
                };
                break;
            case MimirSceneEditorGizmoMode.Scale:
                var scaleDelta = MathF.Exp((screenDelta.X - screenDelta.Y) * 0.006f);
                var current = PlacementForNode(selected);
                SetNodeProgramPlacement(selected, ResizeProgramPlacement(selected, current, current.Width * scaleDelta, resizeWidth: true));
                break;
        }
    }

    private bool IsSelected(MimirSceneEditorNode node) =>
        string.Equals(node.Id, SelectedNodeId, StringComparison.Ordinal);

    private static void AppendNodeFrame(List<AquariumSpline3D> splines, MimirSceneEditorNode node, bool selected)
    {
        var corners = Corners(node.Transform);
        var color = node.Kind switch
        {
            MimirSceneEditorNodeKind.SensorFeedPanel => new Vector4(0.25f, 0.85f, 1.0f, selected ? 1.0f : 0.62f),
            MimirSceneEditorNodeKind.SdfTextPanel => new Vector4(1.0f, 0.78f, 0.28f, selected ? 1.0f : 0.64f),
            MimirSceneEditorNodeKind.Model => new Vector4(0.8f, 0.6f, 1.0f, selected ? 1.0f : 0.58f),
            _ => new Vector4(0.8f, 0.8f, 0.8f, 0.6f),
        };
        var vertices = new[]
        {
            new AquariumSplineVertex(corners[0], color),
            new AquariumSplineVertex(corners[1], color),
            new AquariumSplineVertex(corners[2], color),
            new AquariumSplineVertex(corners[3], color),
            new AquariumSplineVertex(corners[0], color),
        };
        splines.Add(new AquariumSpline3D(
            $"mimir-editor-node:{node.Id}",
            vertices,
            new AquariumSplineStyle(selected ? 0.028f : 0.018f, selected ? 2.1f : 1.1f, color.W, 1.0f, 0.08f),
            CatmullRomSubdivisions: 1));
    }

    private static void AppendGizmo(List<AquariumSpline3D> splines, MimirSceneEditorNode node)
    {
        var center = node.Transform.Position;
        splines.Add(Line($"mimir-editor-gizmo:{node.Id}:x", center, center + new Vector3(1.1f, 0.0f, 0.0f), new Vector4(1.0f, 0.22f, 0.18f, 1.0f)));
        splines.Add(Line($"mimir-editor-gizmo:{node.Id}:y", center, center + new Vector3(0.0f, 1.1f, 0.0f), new Vector4(0.25f, 1.0f, 0.32f, 1.0f)));
        var ring = new List<AquariumSplineVertex>();
        for (var index = 0; index <= 32; index++)
        {
            var angle = index / 32.0f * MathF.Tau;
            ring.Add(new AquariumSplineVertex(
                center + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.0f) * 0.85f,
                new Vector4(0.95f, 0.72f, 0.2f, 0.72f)));
        }

        splines.Add(new AquariumSpline3D(
            $"mimir-editor-gizmo:{node.Id}:rotate",
            ring,
            new AquariumSplineStyle(0.012f, 1.4f, 0.72f, 1.0f, 0.08f),
            CatmullRomSubdivisions: 1));
    }

    private static AquariumSpline3D Line(string id, Vector3 start, Vector3 end, Vector4 color) =>
        new(
            id,
            [new AquariumSplineVertex(start, color), new AquariumSplineVertex(end, color)],
            new AquariumSplineStyle(0.022f, 2.0f, color.W, 1.0f, 0.08f),
            CatmullRomSubdivisions: 1);

    private static Vector3[] Corners(MimirSceneEditorTransform transform)
    {
        var half = transform.Scale * 0.5f;
        var local = new[]
        {
            new Vector2(-half.X, -half.Y),
            new Vector2(half.X, -half.Y),
            new Vector2(half.X, half.Y),
            new Vector2(-half.X, half.Y),
        };
        var sine = MathF.Sin(transform.RotationRadians);
        var cosine = MathF.Cos(transform.RotationRadians);
        return local
            .Select(point => new Vector3(
                transform.Position.X + point.X * cosine - point.Y * sine,
                transform.Position.Y + point.X * sine + point.Y * cosine,
                transform.Position.Z))
            .ToArray();
    }

    private static string FeedNodeId(string sourceId) =>
        $"feed:{sourceId}";

    private static float ProgramAspectRatioFor(MimirRollingStreamBuffer buffer)
    {
        var frame = buffer.Latest?.VideoFrame;
        if (frame is { Width: > 0, Height: > 0 })
        {
            return Math.Clamp(frame.Width / (float)frame.Height / ProgramAspect, 0.05f, 32.0f);
        }

        return 1.0f;
    }

    private static float ProgramPlacementAspectFor(MimirSceneEditorNode node) =>
        Math.Clamp(node.ProgramAspectRatio, 0.05f, 32.0f);

    private static MimirCompositorPlacement ResizeProgramPlacement(MimirSceneEditorNode node, MimirCompositorPlacement placement, float value, bool resizeWidth)
    {
        var aspect = ProgramPlacementAspectFor(node);
        var width = placement.Width;
        var height = placement.Height;
        if (resizeWidth)
        {
            width = Math.Clamp(value, MinProgramExtent, 1.0f);
            height = width / aspect;
            if (height > 1.0f)
            {
                height = 1.0f;
                width = height * aspect;
            }
        }
        else
        {
            height = Math.Clamp(value, MinProgramExtent, 1.0f);
            width = height * aspect;
            if (width > 1.0f)
            {
                width = 1.0f;
                height = width / aspect;
            }
        }

        return placement with { Width = width, Height = height };
    }

    private static MimirCompositorPlacement ResizeProgramPlacementFromHandle(
        MimirSceneEditorNode node,
        MimirCompositorPlacement placement,
        string handle,
        float deltaX,
        float deltaY)
    {
        var west = handle.Contains('w', StringComparison.Ordinal);
        var east = handle.Contains('e', StringComparison.Ordinal);
        var north = handle.Contains('n', StringComparison.Ordinal);
        var south = handle.Contains('s', StringComparison.Ordinal);
        if ((!west && !east) || (!north && !south))
        {
            return placement;
        }

        var l = placement.CenterX - placement.Width * 0.5f;
        var r = placement.CenterX + placement.Width * 0.5f;
        var t = placement.CenterY - placement.Height * 0.5f;
        var b = placement.CenterY + placement.Height * 0.5f;
        var targetWidthFromX = placement.Width + (east ? deltaX : -deltaX);
        var targetHeightFromY = placement.Height + (south ? deltaY : -deltaY);
        var aspect = ProgramPlacementAspectFor(node);
        var xChange = MathF.Abs(targetWidthFromX - placement.Width);
        var yChange = MathF.Abs(targetHeightFromY - placement.Height);
        var targetWidth = xChange >= yChange
            ? targetWidthFromX
            : targetHeightFromY * aspect;
        var resized = ResizeProgramPlacement(node, placement, targetWidth, resizeWidth: true);
        var centerX = west ? r - resized.Width * 0.5f : l + resized.Width * 0.5f;
        var centerY = north ? b - resized.Height * 0.5f : t + resized.Height * 0.5f;
        return resized with
        {
            CenterX = centerX,
            CenterY = centerY,
        };
    }

    private MimirSceneEditorNode? NodeForSource(string sourceId) =>
        nodes.FirstOrDefault(node =>
            node.Kind == MimirSceneEditorNodeKind.SensorFeedPanel &&
            string.Equals(node.SourceId, sourceId, StringComparison.Ordinal));

    private static MimirCompositorPlacement PlacementForNode(MimirSceneEditorNode node)
    {
        var transform = node.Transform;
        return new MimirCompositorPlacement(
            CenterX: Math.Clamp(0.5f + transform.Position.X / ProgramWorldWidth, 0.0f, 1.0f),
            CenterY: Math.Clamp(0.5f - transform.Position.Y / ProgramWorldHeight, 0.0f, 1.0f),
            Width: Math.Clamp(transform.Scale.X / ProgramWorldWidth, MinProgramExtent, 1.0f),
            Height: Math.Clamp(transform.Scale.Y / ProgramWorldHeight, MinProgramExtent, 1.0f),
            RotationRadians: transform.RotationRadians,
            Layer: node.Layer);
    }

    private static void SetNodeProgramPlacement(MimirSceneEditorNode node, MimirCompositorPlacement placement)
    {
        if (node.Locked)
        {
            return;
        }

        var normalized = NormalizeProgramPlacement(placement);
        node.Transform = node.Transform with
        {
            Position = new Vector3((normalized.CenterX - 0.5f) * ProgramWorldWidth, (0.5f - normalized.CenterY) * ProgramWorldHeight, node.Transform.Position.Z),
            Scale = new Vector2(normalized.Width * ProgramWorldWidth, normalized.Height * ProgramWorldHeight),
            RotationRadians = normalized.RotationRadians,
        };
    }

    private static MimirCompositorPlacement NormalizeProgramPlacement(MimirCompositorPlacement placement)
    {
        var width = SnapExtent(Math.Clamp(placement.Width, MinProgramExtent, 1.0f));
        var height = SnapExtent(Math.Clamp(placement.Height, MinProgramExtent, 1.0f));
        var centerX = SnapCenter(Math.Clamp(placement.CenterX, width * 0.5f, 1.0f - width * 0.5f), width);
        var centerY = SnapCenter(Math.Clamp(placement.CenterY, height * 0.5f, 1.0f - height * 0.5f), height);
        return placement with
        {
            CenterX = centerX,
            CenterY = centerY,
            Width = width,
            Height = height,
        };
    }

    private static float SnapExtent(float value) =>
        MathF.Abs(1.0f - value) <= ProgramSnapThreshold ? 1.0f : value;

    private static float SnapCenter(float center, float extent)
    {
        var half = extent * 0.5f;
        if (MathF.Abs(center - 0.5f) <= ProgramSnapThreshold)
        {
            return 0.5f;
        }

        if (MathF.Abs(center - half) <= ProgramSnapThreshold)
        {
            return half;
        }

        if (MathF.Abs(1.0f - half - center) <= ProgramSnapThreshold)
        {
            return 1.0f - half;
        }

        return center;
    }
}

public sealed class MimirSceneEditorNode(
    string id,
    string parentId,
    string displayName,
    MimirSceneEditorNodeKind kind,
    MimirSceneEditorTransform defaultTransform)
{
    public string Id { get; } = id;

    public string ParentId { get; } = parentId;

    public string DisplayName { get; } = displayName;

    public MimirSceneEditorNodeKind Kind { get; } = kind;

    public MimirSceneEditorTransform DefaultTransform { get; } = defaultTransform;

    public MimirSceneEditorTransform Transform { get; set; } = defaultTransform;

    public bool Visible { get; set; } = true;

    public bool Locked { get; set; }

    public int Layer { get; set; }

    public string SourceId { get; init; } = "";

    public float ProgramAspectRatio { get; init; } = Math.Clamp(defaultTransform.Scale.X / Math.Max(defaultTransform.Scale.Y, 0.001f), 0.05f, 32.0f);

    public string Text { get; set; } = "";

    public string ModelPath { get; init; } = "";
}

public readonly record struct MimirSceneEditorTransform(
    Vector3 Position,
    float RotationRadians,
    Vector2 Scale);

public enum MimirSceneEditorNodeKind
{
    Camera,
    SensorFeedPanel,
    SdfTextPanel,
    Model,
}

public enum MimirSceneEditorGizmoMode
{
    Translate,
    Rotate,
    Scale,
}
