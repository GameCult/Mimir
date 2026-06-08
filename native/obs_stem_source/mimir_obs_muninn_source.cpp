#include <obs-module.h>

#include <algorithm>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

namespace {

struct MuninnStreamOption {
    std::string stream_id;
    std::string label;
    std::string url;
    std::string state;
};

struct MuninnSource {
    obs_source_t *source = nullptr;
    obs_source_t *media = nullptr;
    std::string catalog_path = "C:\\Meta\\Odin\\state\\muninn-obs-streams.tsv";
    std::string stream_id;
    std::string stream_url;
};

static std::string obs_string(obs_data_t *settings, const char *key, const char *fallback)
{
    const char *value = obs_data_get_string(settings, key);
    return value && *value ? std::string(value) : std::string(fallback);
}

static std::vector<std::string> split_tsv(const std::string &line)
{
    std::vector<std::string> fields;
    std::string field;
    std::stringstream stream(line);
    while (std::getline(stream, field, '\t')) {
        fields.push_back(field);
    }
    return fields;
}

static std::vector<MuninnStreamOption> read_catalog(const std::string &path)
{
    std::ifstream file(path);
    std::vector<MuninnStreamOption> options;
    if (!file) {
        return options;
    }

    std::string line;
    while (std::getline(file, line)) {
        if (line.empty() || line[0] == '#') {
            continue;
        }

        auto fields = split_tsv(line);
        if (fields.size() < 3 || fields[0].empty()) {
            continue;
        }

        MuninnStreamOption option;
        option.stream_id = fields[0];
        option.label = fields.size() > 1 && !fields[1].empty() ? fields[1] : fields[0];
        option.url = fields[2];
        option.state = fields.size() > 3 ? fields[3] : "unknown";
        options.push_back(option);
    }

    std::sort(options.begin(), options.end(), [](const auto &left, const auto &right) {
        return left.label < right.label;
    });
    return options;
}

static const MuninnStreamOption *find_selected(const std::vector<MuninnStreamOption> &options, const std::string &stream_id)
{
    for (const auto &option : options) {
        if (option.stream_id == stream_id) {
            return &option;
        }
    }
    return options.empty() ? nullptr : &options.front();
}

static void release_media(MuninnSource *ctx)
{
    if (!ctx->media) {
        return;
    }

    obs_source_remove_active_child(ctx->source, ctx->media);
    obs_source_release(ctx->media);
    ctx->media = nullptr;
}

static void create_or_update_media(MuninnSource *ctx, const std::string &url)
{
    if (url.empty()) {
        release_media(ctx);
        ctx->stream_url.clear();
        return;
    }

    if (ctx->media && ctx->stream_url == url) {
        return;
    }

    release_media(ctx);

    obs_data_t *settings = obs_data_create();
    obs_data_set_bool(settings, "is_local_file", false);
    obs_data_set_string(settings, "input", url.c_str());
    obs_data_set_bool(settings, "restart_on_activate", true);
    obs_data_set_bool(settings, "clear_on_media_end", false);
    obs_data_set_bool(settings, "close_when_inactive", false);

    ctx->media = obs_source_create_private("ffmpeg_source", "Muninn Stream Media", settings);
    obs_data_release(settings);
    if (ctx->media) {
        obs_source_add_active_child(ctx->source, ctx->media);
        ctx->stream_url = url;
    } else {
        ctx->stream_url.clear();
        blog(LOG_WARNING, "Muninn Stream could not create OBS ffmpeg_source for %s", url.c_str());
    }
}

static void populate_stream_list(obs_property_t *property, const std::string &catalog_path)
{
    obs_property_list_clear(property);
    const auto options = read_catalog(catalog_path);
    if (options.empty()) {
        obs_property_list_add_string(property, "No active Muninn streams", "");
        return;
    }

    for (const auto &option : options) {
        std::string label = option.label;
        if (!option.state.empty()) {
            label += " [" + option.state + "]";
        }
        if (option.url.empty()) {
            label += " (activation required)";
        }
        obs_property_list_add_string(property, label.c_str(), option.stream_id.c_str());
    }
}

static bool muninn_refresh_streams(obs_properties_t *props, obs_property_t *, void *data)
{
    std::string catalog_path = "C:\\Meta\\Odin\\state\\muninn-obs-streams.tsv";
    if (data) {
        auto *ctx = static_cast<MuninnSource *>(data);
        catalog_path = ctx->catalog_path;
    }

    obs_property_t *stream_list = obs_properties_get(props, "stream_id");
    if (stream_list) {
        populate_stream_list(stream_list, catalog_path);
    }
    return true;
}

static const char *muninn_get_name(void *)
{
    return "Muninn Stream";
}

static void muninn_update(void *data, obs_data_t *settings)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    ctx->catalog_path = obs_string(settings, "catalog_path", "C:\\Meta\\Odin\\state\\muninn-obs-streams.tsv");
    ctx->stream_id = obs_string(settings, "stream_id", "");

    const auto options = read_catalog(ctx->catalog_path);
    const auto *selected = find_selected(options, ctx->stream_id);
    if (!selected) {
        create_or_update_media(ctx, "");
        return;
    }

    ctx->stream_id = selected->stream_id;
    create_or_update_media(ctx, selected->url);
}

static void *muninn_create(obs_data_t *settings, obs_source_t *source)
{
    auto *ctx = new MuninnSource();
    ctx->source = source;
    muninn_update(ctx, settings);
    return ctx;
}

static void muninn_destroy(void *data)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    release_media(ctx);
    delete ctx;
}

static uint32_t muninn_width(void *data)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    return ctx->media ? obs_source_get_width(ctx->media) : 0;
}

static uint32_t muninn_height(void *data)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    return ctx->media ? obs_source_get_height(ctx->media) : 0;
}

static void muninn_defaults(obs_data_t *settings)
{
    obs_data_set_default_string(settings, "catalog_path", "C:\\Meta\\Odin\\state\\muninn-obs-streams.tsv");
    obs_data_set_default_string(settings, "stream_id", "");
}

static obs_properties_t *muninn_properties(void *data)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    const auto catalog_path = ctx ? ctx->catalog_path : std::string("C:\\Meta\\Odin\\state\\muninn-obs-streams.tsv");

    obs_properties_t *props = obs_properties_create();
    obs_properties_add_text(props, "catalog_path", "Muninn OBS catalog", OBS_TEXT_DEFAULT);
    obs_property_t *streams = obs_properties_add_list(props, "stream_id", "Muninn stream", OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
    populate_stream_list(streams, catalog_path);
    obs_properties_add_button(props, "refresh_streams", "Refresh Muninn streams", muninn_refresh_streams);
    return props;
}

static void muninn_render(void *data, gs_effect_t *)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    if (ctx->media) {
        obs_source_video_render(ctx->media);
    }
}

static void muninn_enum_active_sources(void *data, obs_source_enum_proc_t enum_callback, void *param)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    if (ctx->media) {
        enum_callback(ctx->source, ctx->media, param);
    }
}

static obs_source_info muninn_source_info = {
    .id = "muninn_stream_source",
    .type = OBS_SOURCE_TYPE_INPUT,
    .output_flags = OBS_SOURCE_VIDEO | OBS_SOURCE_AUDIO,
    .get_name = muninn_get_name,
    .create = muninn_create,
    .destroy = muninn_destroy,
    .get_width = muninn_width,
    .get_height = muninn_height,
    .get_defaults = muninn_defaults,
    .get_properties = muninn_properties,
    .update = muninn_update,
    .video_render = muninn_render,
    .enum_active_sources = muninn_enum_active_sources,
};

} // namespace

void mimir_register_muninn_source()
{
    obs_register_source(&muninn_source_info);
}
