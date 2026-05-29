using System.Numerics;
using Aquarium.Engine;
using Aquarium.Engine.Input;
using Aquarium.Engine.Render;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirSceneEditorState
{
    private readonly List<MimirSceneEditorNode> nodes = [];
    private readonly Dictionary<string, bool> expandedGroups = new(StringComparer.Ordinal)
    {
        ["scene"] = true,
        ["feeds"] = true,
        ["text"] = true,
        ["models"] = true,
    };

    private string pendingModelPath = "assets/models/example.glb";
    private string pendingText = "Mimir";
    private Vector2? previousMousePosition;

    public MimirSceneEditorState()
    {
        nodes.Add(new MimirSceneEditorNode(
            "editor-camera",
            "",
            "Editor Camera",
            MimirSceneEditorNodeKind.Camera,
            new MimirSceneEditorTransform(new Vector3(0.0f, 0.0f, -18.0f), 0.0f, Vector2.One)));
        nodes.Add(new MimirSceneEditorNode(
            "text-title",
            "",
            "SDF Text Panel",
            MimirSceneEditorNodeKind.SdfTextPanel,
            new MimirSceneEditorTransform(new Vector3(-3.2f, 2.1f, 0.0f), 0.0f, new Vector2(2.4f, 0.8f)))
        {
            Text = pendingText,
        });
        SelectedNodeId = "text-title";
    }

    public bool Enabled { get; set; } = true;

    public MimirSceneEditorGizmoMode GizmoMode { get; set; } = MimirSceneEditorGizmoMode.Translate;

    public string SelectedNodeId { get; private set; }

    public string PendingModelPath
    {
        get => pendingModelPath;
        set => pendingModelPath = value.Trim();
    }

    public string PendingText
    {
        get => pendingText;
        set => pendingText = value;
    }

    public IReadOnlyList<MimirSceneEditorNode> Nodes => nodes;

    public MimirSceneEditorNode? SelectedNode =>
        nodes.FirstOrDefault(node => string.Equals(node.Id, SelectedNodeId, StringComparison.Ordinal));

    public IReadOnlyList<MimirSceneEditorNode> VisibleNodes =>
        nodes.Where(node => node.Visible).ToArray();

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
                SourceId = buffer.Descriptor.SourceId,
            });
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

    public bool IsExpanded(string groupId) =>
        expandedGroups.TryGetValue(groupId, out var expanded) && expanded;

    public void SetExpanded(string groupId, bool expanded)
    {
        expandedGroups[groupId] = expanded;
    }

    public void SelectNode(string nodeId)
    {
        if (nodes.Any(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal)))
        {
            SelectedNodeId = nodeId;
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

    public void AddSdfTextPanel()
    {
        var index = nodes.Count(node => node.Kind == MimirSceneEditorNodeKind.SdfTextPanel) + 1;
        var node = new MimirSceneEditorNode(
            $"text-{index}",
            "text",
            $"SDF Text {index}",
            MimirSceneEditorNodeKind.SdfTextPanel,
            new MimirSceneEditorTransform(new Vector3(-2.8f + index * 0.25f, 1.7f - index * 0.25f, 0.0f), 0.0f, new Vector2(2.4f, 0.8f)))
        {
            Text = string.IsNullOrWhiteSpace(pendingText) ? "Text" : pendingText,
        };
        nodes.Add(node);
        SelectedNodeId = node.Id;
    }

    public void ImportModelPlaceholder()
    {
        var modelPath = string.IsNullOrWhiteSpace(pendingModelPath)
            ? "assets/models/model.glb"
            : pendingModelPath;
        var index = nodes.Count(node => node.Kind == MimirSceneEditorNodeKind.Model) + 1;
        var node = new MimirSceneEditorNode(
            $"model-{index}",
            "models",
            Path.GetFileNameWithoutExtension(modelPath),
            MimirSceneEditorNodeKind.Model,
            new MimirSceneEditorTransform(new Vector3(2.4f, -1.5f + index * 0.2f, 0.0f), 0.0f, new Vector2(1.2f, 1.2f)))
        {
            ModelPath = modelPath,
        };
        nodes.Add(node);
        SelectedNodeId = node.Id;
    }

    public string DescribeHierarchy()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Scene");
        AppendNode(builder, nodes.First(node => node.Kind == MimirSceneEditorNodeKind.Camera), 1);
        AppendGroup(builder, "feeds", "Sensor Feeds", MimirSceneEditorNodeKind.SensorFeedPanel);
        AppendGroup(builder, "text", "SDF Text", MimirSceneEditorNodeKind.SdfTextPanel);
        AppendGroup(builder, "models", "Models", MimirSceneEditorNodeKind.Model);
        return builder.ToString().TrimEnd();
    }

    public string DescribeSelection()
    {
        if (SelectedNode is not { } selected)
        {
            return "no selection";
        }

        var source = string.IsNullOrWhiteSpace(selected.SourceId) ? "" : $" source={selected.SourceId}";
        var model = string.IsNullOrWhiteSpace(selected.ModelPath) ? "" : $" model={selected.ModelPath}";
        return $"{selected.Kind} {selected.DisplayName}{source}{model} pos={selected.Transform.Position.X:0.00},{selected.Transform.Position.Y:0.00},{selected.Transform.Position.Z:0.00} rot={selected.Transform.RotationRadians:0.00} scale={selected.Transform.Scale.X:0.00},{selected.Transform.Scale.Y:0.00}";
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

    private void AppendGroup(System.Text.StringBuilder builder, string groupId, string label, MimirSceneEditorNodeKind kind)
    {
        var groupNodes = nodes.Where(node => node.Kind == kind).OrderBy(node => node.DisplayName, StringComparer.Ordinal).ToArray();
        builder.Append("  ")
            .Append(IsExpanded(groupId) ? "v " : "> ")
            .Append(label)
            .Append(" (")
            .Append(groupNodes.Length)
            .AppendLine(")");
        if (!IsExpanded(groupId))
        {
            return;
        }

        foreach (var node in groupNodes)
        {
            AppendNode(builder, node, 2);
        }
    }

    private void AppendNode(System.Text.StringBuilder builder, MimirSceneEditorNode node, int depth)
    {
        builder
            .Append(' ', depth * 2)
            .Append(IsSelected(node) ? "* " : "- ")
            .Append(node.Visible ? "[eye] " : "[off] ")
            .Append(node.DisplayName)
            .Append(" <")
            .Append(node.Kind)
            .AppendLine(">");
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
        if (SelectedNode is not { } selected || selected.Kind is MimirSceneEditorNodeKind.Camera or MimirSceneEditorNodeKind.SensorFeedPanel)
        {
            return;
        }

        nodes.Remove(selected);
        SelectedNodeId = nodes.First().Id;
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
        var worldDelta = new Vector2(screenDelta.X, -screenDelta.Y) * 0.012f;
        switch (GizmoMode)
        {
            case MimirSceneEditorGizmoMode.Translate:
                selected.Transform = selected.Transform with
                {
                    Position = selected.Transform.Position + new Vector3(worldDelta, 0.0f),
                };
                break;
            case MimirSceneEditorGizmoMode.Rotate:
                selected.Transform = selected.Transform with
                {
                    RotationRadians = selected.Transform.RotationRadians + screenDelta.X * 0.012f,
                };
                break;
            case MimirSceneEditorGizmoMode.Scale:
                var scaleDelta = MathF.Exp((screenDelta.X - screenDelta.Y) * 0.006f);
                selected.Transform = selected.Transform with
                {
                    Scale = Vector2.Clamp(selected.Transform.Scale * scaleDelta, new Vector2(0.05f), new Vector2(8.0f)),
                };
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

    public string SourceId { get; init; } = "";

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
