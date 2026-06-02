#include <obs-module.h>
#include <util/platform.h>

#include <chrono>
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

#include <d3d11_1.h>
#include <d3d12.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <windows.h>

using Microsoft::WRL::ComPtr;

namespace {

struct MimirProgramTextureSource {
    obs_source_t *source = nullptr;
    std::string texture_name = "Global\\MimirFensalirProgramTexture";
    std::string fence_name = "Global\\MimirFensalirProgramFence";
    std::string consumer_fence_name = "Global\\MimirObsProgramConsumerFence";
    uint32_t ring_count = 1;
    uint32_t width = 0;
    uint32_t height = 0;
    uint64_t fence_value = 0;
    uint64_t last_producer_fence_value = 0;
    uint64_t next_retry_ns = 0;
    uint64_t next_failure_log_ns = 0;
    HANDLE bridge_shared_handle = nullptr;
    HANDLE consumer_fence_shared_handle = nullptr;
    HANDLE fence_event = nullptr;
    gs_texture_t *obs_texture = nullptr;
    ComPtr<ID3D12Device> d3d12_device;
    ComPtr<ID3D12CommandQueue> command_queue;
    ComPtr<ID3D12CommandAllocator> command_allocator;
    ComPtr<ID3D12GraphicsCommandList> command_list;
    ComPtr<ID3D12Fence> fence;
    ComPtr<ID3D12Fence> producer_fence;
    ComPtr<ID3D12Fence> consumer_fence;
    std::vector<ComPtr<ID3D12Resource>> source_textures;
    ComPtr<ID3D12Resource> bridge_texture_d3d12;
    ComPtr<ID3D11Device> d3d11_device;
    ComPtr<ID3D11DeviceContext> d3d11_context;
    ComPtr<ID3D11Texture2D> bridge_texture_d3d11;
};

static void log_retry_failure(MimirProgramTextureSource *ctx, const char *message, HRESULT hr = S_OK)
{
    const uint64_t now = os_gettime_ns();
    if (now < ctx->next_failure_log_ns) {
        return;
    }

    ctx->next_failure_log_ns = now + 2000000000ULL;
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: %s: 0x%08lx", message, static_cast<unsigned long>(hr));
    } else {
        blog(LOG_WARNING, "Mimir OBS program texture: %s", message);
    }
}

static std::wstring utf8_to_wide(const std::string &value)
{
    if (value.empty()) {
        return {};
    }

    const int count = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, nullptr, 0);
    if (count <= 0) {
        return {};
    }

    std::wstring wide(static_cast<size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, wide.data(), count);
    if (!wide.empty() && wide.back() == L'\0') {
        wide.pop_back();
    }

    return wide;
}

static std::string obs_string(obs_data_t *settings, const char *key, const char *fallback)
{
    const char *value = obs_data_get_string(settings, key);
    return value && *value ? std::string(value) : std::string(fallback);
}

static uint32_t clamp_ring_count(long long value)
{
    if (value < 1) {
        return 1;
    }

    if (value > 4) {
        return 4;
    }

    return static_cast<uint32_t>(value);
}

static std::string texture_name_for_slot(const MimirProgramTextureSource *ctx, uint32_t slot)
{
    if (ctx->ring_count <= 1) {
        return ctx->texture_name;
    }

    return ctx->texture_name + "." + std::to_string(slot);
}

static void release_bridge(MimirProgramTextureSource *ctx)
{
    if (ctx->obs_texture) {
        gs_texture_destroy(ctx->obs_texture);
        ctx->obs_texture = nullptr;
    }

    ctx->bridge_texture_d3d12.Reset();
    ctx->bridge_texture_d3d11.Reset();
    ctx->source_textures.clear();
    ctx->fence.Reset();
    ctx->producer_fence.Reset();
    ctx->consumer_fence.Reset();
    ctx->command_list.Reset();
    ctx->command_allocator.Reset();
    ctx->command_queue.Reset();
    ctx->d3d12_device.Reset();
    ctx->d3d11_context.Reset();
    ctx->d3d11_device.Reset();
    ctx->width = 0;
    ctx->height = 0;
    ctx->fence_value = 0;
    ctx->last_producer_fence_value = 0;

    if (ctx->bridge_shared_handle) {
        CloseHandle(ctx->bridge_shared_handle);
        ctx->bridge_shared_handle = nullptr;
    }

    if (ctx->consumer_fence_shared_handle) {
        CloseHandle(ctx->consumer_fence_shared_handle);
        ctx->consumer_fence_shared_handle = nullptr;
    }

    if (ctx->fence_event) {
        CloseHandle(ctx->fence_event);
        ctx->fence_event = nullptr;
    }
}

static bool wait_for_copy(MimirProgramTextureSource *ctx, uint64_t consumed_producer_value)
{
    if (ctx->consumer_fence && consumed_producer_value > 0) {
        HRESULT consumer_hr = ctx->command_queue->Signal(ctx->consumer_fence.Get(), consumed_producer_value);
        if (FAILED(consumer_hr)) {
            blog(LOG_WARNING,
                 "Mimir OBS program texture: consumer fence Signal failed: 0x%08lx",
                 static_cast<unsigned long>(consumer_hr));
        }
    }

    const uint64_t value = ++ctx->fence_value;
    HRESULT hr = ctx->command_queue->Signal(ctx->fence.Get(), value);
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: D3D12 Signal failed: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    if (ctx->fence->GetCompletedValue() >= value) {
        return true;
    }

    hr = ctx->fence->SetEventOnCompletion(value, ctx->fence_event);
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: SetEventOnCompletion failed: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    WaitForSingleObject(ctx->fence_event, 100);
    return ctx->fence->GetCompletedValue() >= value;
}

static bool create_devices(MimirProgramTextureSource *ctx)
{
    HRESULT hr = D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&ctx->d3d12_device));
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: D3D12CreateDevice failed: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    D3D12_COMMAND_QUEUE_DESC queue_desc = {};
    queue_desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    hr = ctx->d3d12_device->CreateCommandQueue(&queue_desc, IID_PPV_ARGS(&ctx->command_queue));
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: CreateCommandQueue failed: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    hr = ctx->d3d12_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&ctx->command_allocator));
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: CreateCommandAllocator failed: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    hr = ctx->d3d12_device->CreateCommandList(
        0,
        D3D12_COMMAND_LIST_TYPE_DIRECT,
        ctx->command_allocator.Get(),
        nullptr,
        IID_PPV_ARGS(&ctx->command_list));
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: CreateCommandList failed: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    ctx->command_list->Close();

    hr = ctx->d3d12_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&ctx->fence));
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: CreateFence failed: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    ctx->fence_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!ctx->fence_event) {
        blog(LOG_WARNING, "Mimir OBS program texture: CreateEvent failed: %lu", GetLastError());
        return false;
    }

    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL feature_levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    D3D_FEATURE_LEVEL selected = D3D_FEATURE_LEVEL_11_0;
    hr = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        flags,
        feature_levels,
        2,
        D3D11_SDK_VERSION,
        &ctx->d3d11_device,
        &selected,
        &ctx->d3d11_context);
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: D3D11CreateDevice failed: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    return true;
}

static void create_consumer_fence(MimirProgramTextureSource *ctx)
{
    if (ctx->consumer_fence_name.empty()) {
        return;
    }

    HRESULT hr = ctx->d3d12_device->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&ctx->consumer_fence));
    if (FAILED(hr)) {
        log_retry_failure(ctx, "CreateFence consumer fence failed", hr);
        return;
    }

    const auto wide_name = utf8_to_wide(ctx->consumer_fence_name);
    if (wide_name.empty()) {
        log_retry_failure(ctx, "consumer fence name is empty or invalid UTF-8");
        ctx->consumer_fence.Reset();
        return;
    }

    hr = ctx->d3d12_device->CreateSharedHandle(
        ctx->consumer_fence.Get(),
        nullptr,
        GENERIC_ALL,
        wide_name.c_str(),
        &ctx->consumer_fence_shared_handle);
    if (FAILED(hr)) {
        log_retry_failure(ctx, "CreateSharedHandle consumer fence failed", hr);
        ctx->consumer_fence.Reset();
        return;
    }

    blog(LOG_INFO, "Mimir OBS program texture consumer fence published %s", ctx->consumer_fence_name.c_str());
}

static bool open_source_texture(MimirProgramTextureSource *ctx)
{
    ctx->source_textures.clear();
    ctx->source_textures.reserve(ctx->ring_count);
    for (uint32_t slot = 0; slot < ctx->ring_count; ++slot) {
        const auto slot_name = texture_name_for_slot(ctx, slot);
        const auto wide_name = utf8_to_wide(slot_name);
        if (wide_name.empty()) {
            log_retry_failure(ctx, "shared texture name is empty or invalid UTF-8");
            return false;
        }

        HANDLE source_handle = nullptr;
        HRESULT hr = ctx->d3d12_device->OpenSharedHandleByName(wide_name.c_str(), GENERIC_ALL, &source_handle);
        if (FAILED(hr)) {
            log_retry_failure(ctx, "OpenSharedHandleByName failed", hr);
            return false;
        }

        ComPtr<ID3D12Resource> source_texture;
        hr = ctx->d3d12_device->OpenSharedHandle(source_handle, IID_PPV_ARGS(&source_texture));
        CloseHandle(source_handle);
        if (FAILED(hr)) {
            log_retry_failure(ctx, "OpenSharedHandle failed", hr);
            return false;
        }

        const D3D12_RESOURCE_DESC desc = source_texture->GetDesc();
        if (desc.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D || desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) {
            log_retry_failure(ctx, "expected BGRA8 D3D12 Texture2D");
            return false;
        }

        const uint32_t slot_width = static_cast<uint32_t>(desc.Width);
        const uint32_t slot_height = desc.Height;
        if (slot_width == 0 || slot_height == 0) {
            log_retry_failure(ctx, "shared texture dimensions are empty");
            return false;
        }

        if (slot == 0) {
            ctx->width = slot_width;
            ctx->height = slot_height;
        } else if (slot_width != ctx->width || slot_height != ctx->height) {
            log_retry_failure(ctx, "program output ring texture dimensions do not match");
            return false;
        }

        ctx->source_textures.push_back(std::move(source_texture));
    }

    return ctx->source_textures.size() == ctx->ring_count;
}

static bool open_producer_fence(MimirProgramTextureSource *ctx)
{
    const auto wide_name = utf8_to_wide(ctx->fence_name);
    if (wide_name.empty()) {
        return true;
    }

    HANDLE fence_handle = nullptr;
    HRESULT hr = ctx->d3d12_device->OpenSharedHandleByName(wide_name.c_str(), GENERIC_ALL, &fence_handle);
    if (FAILED(hr)) {
        log_retry_failure(ctx, "OpenSharedHandleByName producer fence failed", hr);
        return true;
    }

    hr = ctx->d3d12_device->OpenSharedHandle(fence_handle, IID_PPV_ARGS(&ctx->producer_fence));
    CloseHandle(fence_handle);
    if (FAILED(hr)) {
        log_retry_failure(ctx, "OpenSharedHandle producer fence failed", hr);
        ctx->producer_fence.Reset();
        return true;
    }

    ctx->last_producer_fence_value = ctx->producer_fence->GetCompletedValue();
    if (ctx->last_producer_fence_value == UINT64_MAX) {
        log_retry_failure(ctx, "producer fence opened in device-removed state");
        ctx->producer_fence.Reset();
        return false;
    }

    blog(LOG_INFO,
         "Mimir OBS program texture producer fence opened %s value=%llu",
         ctx->fence_name.c_str(),
         static_cast<unsigned long long>(ctx->last_producer_fence_value));
    return true;
}

static bool read_producer_fence(MimirProgramTextureSource *ctx, uint64_t *completed)
{
    *completed = 0;
    if (!ctx->producer_fence) {
        return true;
    }

    *completed = ctx->producer_fence->GetCompletedValue();
    if (*completed == UINT64_MAX) {
        log_retry_failure(ctx, "producer fence reported device removal");
        return false;
    }

    if (*completed > ctx->last_producer_fence_value) {
        ctx->last_producer_fence_value = *completed;
    }

    return true;
}

static bool create_obs_bridge_texture(MimirProgramTextureSource *ctx)
{
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = ctx->width;
    desc.Height = ctx->height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED;

    HRESULT hr = ctx->d3d11_device->CreateTexture2D(&desc, nullptr, &ctx->bridge_texture_d3d11);
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: CreateTexture2D bridge failed: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    ComPtr<IDXGIResource> legacy_resource;
    hr = ctx->bridge_texture_d3d11.As(&legacy_resource);
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: bridge texture is not IDXGIResource: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    hr = legacy_resource->GetSharedHandle(&ctx->bridge_shared_handle);
    if (FAILED(hr) || !ctx->bridge_shared_handle) {
        blog(LOG_WARNING, "Mimir OBS program texture: GetSharedHandle bridge failed: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    hr = ctx->d3d12_device->OpenSharedHandle(ctx->bridge_shared_handle, IID_PPV_ARGS(&ctx->bridge_texture_d3d12));
    if (FAILED(hr)) {
        blog(LOG_WARNING, "Mimir OBS program texture: D3D12 open legacy bridge handle failed: 0x%08lx", static_cast<unsigned long>(hr));
        return false;
    }

    ctx->obs_texture = gs_texture_open_shared(static_cast<uint32_t>(reinterpret_cast<uintptr_t>(ctx->bridge_shared_handle)));
    if (!ctx->obs_texture) {
        blog(LOG_WARNING, "Mimir OBS program texture: OBS failed to open legacy bridge shared texture");
        return false;
    }

    return true;
}

static bool ensure_bridge(MimirProgramTextureSource *ctx)
{
    if (ctx->obs_texture) {
        return true;
    }

    const uint64_t now = os_gettime_ns();
    if (now < ctx->next_retry_ns) {
        return false;
    }

    ctx->next_retry_ns = now + 1000000000ULL;
    release_bridge(ctx);

    if (!create_devices(ctx)) {
        release_bridge(ctx);
        return false;
    }

    create_consumer_fence(ctx);
    if (!open_source_texture(ctx) || !create_obs_bridge_texture(ctx)) {
        release_bridge(ctx);
        return false;
    }

    if (!open_producer_fence(ctx)) {
        release_bridge(ctx);
        return false;
    }

    blog(LOG_INFO, "Mimir OBS program texture source opened %s (%ux%u ring=%u)",
         ctx->texture_name.c_str(),
         ctx->width,
         ctx->height,
         ctx->ring_count);
    return true;
}

static bool copy_source_to_bridge(MimirProgramTextureSource *ctx)
{
    HRESULT hr = ctx->command_allocator->Reset();
    if (FAILED(hr)) {
        return false;
    }

    uint64_t completed_producer_value = 0;
    if (!read_producer_fence(ctx, &completed_producer_value)) {
        release_bridge(ctx);
        return false;
    }

    const uint32_t source_slot = ctx->source_textures.empty()
        ? 0
        : static_cast<uint32_t>(completed_producer_value % ctx->source_textures.size());
    ID3D12Resource *source_texture = ctx->source_textures[source_slot].Get();

    hr = ctx->command_list->Reset(ctx->command_allocator.Get(), nullptr);
    if (FAILED(hr)) {
        return false;
    }

    D3D12_RESOURCE_BARRIER barriers[2] = {};
    barriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barriers[0].Transition.pResource = source_texture;
    barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barriers[1].Transition.pResource = ctx->bridge_texture_d3d12.Get();
    barriers[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    barriers[1].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
    barriers[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    ctx->command_list->ResourceBarrier(2, barriers);
    ctx->command_list->CopyResource(ctx->bridge_texture_d3d12.Get(), source_texture);

    std::swap(barriers[0].Transition.StateBefore, barriers[0].Transition.StateAfter);
    std::swap(barriers[1].Transition.StateBefore, barriers[1].Transition.StateAfter);
    ctx->command_list->ResourceBarrier(2, barriers);

    hr = ctx->command_list->Close();
    if (FAILED(hr)) {
        return false;
    }

    ID3D12CommandList *lists[] = {ctx->command_list.Get()};
    ctx->command_queue->ExecuteCommandLists(1, lists);
    return wait_for_copy(ctx, completed_producer_value);
}

static const char *mimir_program_texture_get_name(void *)
{
    return "Mimir Program Texture";
}

static void mimir_program_texture_update(void *data, obs_data_t *settings)
{
    auto *ctx = static_cast<MimirProgramTextureSource *>(data);
    ctx->texture_name = obs_string(settings, "texture_name", "Global\\MimirFensalirProgramTexture");
    ctx->fence_name = obs_string(settings, "fence_name", "Global\\MimirFensalirProgramFence");
    ctx->consumer_fence_name = obs_string(settings, "consumer_fence_name", "Global\\MimirObsProgramConsumerFence");
    ctx->ring_count = clamp_ring_count(obs_data_get_int(settings, "ring_count"));
    release_bridge(ctx);
}

static void *mimir_program_texture_create(obs_data_t *settings, obs_source_t *source)
{
    auto *ctx = new MimirProgramTextureSource();
    ctx->source = source;
    mimir_program_texture_update(ctx, settings);
    return ctx;
}

static void mimir_program_texture_destroy(void *data)
{
    auto *ctx = static_cast<MimirProgramTextureSource *>(data);
    release_bridge(ctx);
    delete ctx;
}

static uint32_t mimir_program_texture_width(void *data)
{
    auto *ctx = static_cast<MimirProgramTextureSource *>(data);
    ensure_bridge(ctx);
    return ctx->width;
}

static uint32_t mimir_program_texture_height(void *data)
{
    auto *ctx = static_cast<MimirProgramTextureSource *>(data);
    ensure_bridge(ctx);
    return ctx->height;
}

static void mimir_program_texture_defaults(obs_data_t *settings)
{
    obs_data_set_default_string(settings, "texture_name", "Global\\MimirFensalirProgramTexture");
    obs_data_set_default_string(settings, "fence_name", "Global\\MimirFensalirProgramFence");
    obs_data_set_default_string(settings, "consumer_fence_name", "Global\\MimirObsProgramConsumerFence");
    obs_data_set_default_int(settings, "ring_count", 1);
}

static obs_properties_t *mimir_program_texture_properties(void *)
{
    obs_properties_t *props = obs_properties_create();
    obs_properties_add_text(props, "texture_name", "Shared D3D12 texture name", OBS_TEXT_DEFAULT);
    obs_properties_add_text(props, "fence_name", "Shared D3D12 fence name", OBS_TEXT_DEFAULT);
    obs_properties_add_text(props, "consumer_fence_name", "Consumer D3D12 fence name", OBS_TEXT_DEFAULT);
    obs_properties_add_int(props, "ring_count", "Texture ring slots", 1, 4, 1);
    return props;
}

static void mimir_program_texture_render(void *data, gs_effect_t *)
{
    auto *ctx = static_cast<MimirProgramTextureSource *>(data);
    if (!ensure_bridge(ctx)) {
        return;
    }

    if (copy_source_to_bridge(ctx)) {
        obs_source_draw(ctx->obs_texture, 0, 0, ctx->width, ctx->height, false);
    }
}

static obs_source_info mimir_program_texture_source_info = {
    .id = "mimir_program_texture_source",
    .type = OBS_SOURCE_TYPE_INPUT,
    .output_flags = OBS_SOURCE_VIDEO,
    .get_name = mimir_program_texture_get_name,
    .create = mimir_program_texture_create,
    .destroy = mimir_program_texture_destroy,
    .get_width = mimir_program_texture_width,
    .get_height = mimir_program_texture_height,
    .get_defaults = mimir_program_texture_defaults,
    .get_properties = mimir_program_texture_properties,
    .update = mimir_program_texture_update,
    .video_render = mimir_program_texture_render,
};

} // namespace

void mimir_register_program_texture_source()
{
    obs_register_source(&mimir_program_texture_source_info);
}
