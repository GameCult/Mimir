#include <obs-module.h>

#include <algorithm>
#include <cstdint>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <vector>

namespace {

constexpr const char *MuninnStorePath = "C:\\Meta\\Odin\\state\\muninn.telemetry.cc";
constexpr const char *MuninnObsCatalogType = "muninn.obs_stream_catalog";
constexpr const char *MuninnObsCatalogSchema = "muninn.obs_stream_catalog.v1";
constexpr const char *MuninnObsCatalogKey = "obs";

struct MuninnStreamOption {
    std::string stream_id;
    std::string label;
    std::string url;
    std::string state;
};

struct MuninnSource {
    obs_source_t *source = nullptr;
    obs_source_t *media = nullptr;
    std::string store_path = MuninnStorePath;
    std::string stream_id;
    std::string stream_url;
};

static std::string obs_string(obs_data_t *settings, const char *key, const char *fallback)
{
    const char *value = obs_data_get_string(settings, key);
    return value && *value ? std::string(value) : std::string(fallback);
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

class MsgpackReader {
public:
    explicit MsgpackReader(const std::vector<uint8_t> &bytes) : data_(bytes.data()), size_(bytes.size()) {}
    MsgpackReader(const uint8_t *data, size_t size) : data_(data), size_(size) {}

    uint32_t read_array_len()
    {
        const uint8_t tag = read_u8();
        if ((tag & 0xf0) == 0x90) {
            return tag & 0x0f;
        }
        if (tag == 0xdc) {
            return read_u16();
        }
        if (tag == 0xdd) {
            return read_u32();
        }
        throw std::runtime_error("expected MessagePack array");
    }

    std::string read_string()
    {
        const uint8_t tag = read_u8();
        uint32_t length = 0;
        if ((tag & 0xe0) == 0xa0) {
            length = tag & 0x1f;
        } else if (tag == 0xd9) {
            length = read_u8();
        } else if (tag == 0xda) {
            length = read_u16();
        } else if (tag == 0xdb) {
            length = read_u32();
        } else {
            throw std::runtime_error("expected MessagePack string");
        }
        require(length);
        std::string value(reinterpret_cast<const char *>(data_ + pos_), length);
        pos_ += length;
        return value;
    }

    std::vector<std::string> read_string_array()
    {
        const uint32_t length = read_array_len();
        std::vector<std::string> values;
        values.reserve(length);
        for (uint32_t index = 0; index < length; ++index) {
            values.push_back(read_string());
        }
        return values;
    }

    std::vector<uint8_t> read_bytes()
    {
        const uint8_t tag = read_u8();
        uint32_t length = 0;
        if (tag == 0xc4) {
            length = read_u8();
        } else if (tag == 0xc5) {
            length = read_u16();
        } else if (tag == 0xc6) {
            length = read_u32();
        } else if ((tag & 0xf0) == 0x90 || tag == 0xdc || tag == 0xdd) {
            pos_--;
            length = read_array_len();
            std::vector<uint8_t> bytes;
            bytes.reserve(length);
            for (uint32_t index = 0; index < length; ++index) {
                bytes.push_back(static_cast<uint8_t>(read_unsigned()));
            }
            return bytes;
        } else {
            throw std::runtime_error("expected MessagePack bytes");
        }
        require(length);
        std::vector<uint8_t> bytes(data_ + pos_, data_ + pos_ + length);
        pos_ += length;
        return bytes;
    }

    uint64_t read_unsigned()
    {
        const uint8_t tag = read_u8();
        if (tag <= 0x7f) {
            return tag;
        }
        if (tag == 0xcc) {
            return read_u8();
        }
        if (tag == 0xcd) {
            return read_u16();
        }
        if (tag == 0xce) {
            return read_u32();
        }
        if (tag == 0xcf) {
            return read_u64();
        }
        throw std::runtime_error("expected MessagePack unsigned integer");
    }

    void skip()
    {
        const uint8_t tag = read_u8();
        if (tag <= 0x7f || tag >= 0xe0 || tag == 0xc0 || tag == 0xc2 || tag == 0xc3) {
            return;
        }
        if ((tag & 0xe0) == 0xa0) {
            skip_bytes(tag & 0x1f);
            return;
        }
        if ((tag & 0xf0) == 0x90) {
            skip_items(tag & 0x0f);
            return;
        }
        if ((tag & 0xf0) == 0x80) {
            skip_items((tag & 0x0f) * 2);
            return;
        }
        switch (tag) {
        case 0xc4:
        case 0xd9:
            skip_bytes(read_u8());
            return;
        case 0xc5:
        case 0xda:
            skip_bytes(read_u16());
            return;
        case 0xc6:
        case 0xdb:
            skip_bytes(read_u32());
            return;
        case 0xcc:
        case 0xd0:
            skip_bytes(1);
            return;
        case 0xcd:
        case 0xd1:
            skip_bytes(2);
            return;
        case 0xce:
        case 0xd2:
        case 0xca:
            skip_bytes(4);
            return;
        case 0xcf:
        case 0xd3:
        case 0xcb:
            skip_bytes(8);
            return;
        case 0xdc:
            skip_items(read_u16());
            return;
        case 0xdd:
            skip_items(read_u32());
            return;
        case 0xde:
            skip_items(read_u16() * 2);
            return;
        case 0xdf:
            skip_items(read_u32() * 2);
            return;
        default:
            throw std::runtime_error("unsupported MessagePack tag");
        }
    }

private:
    const uint8_t *data_;
    size_t size_;
    size_t pos_ = 0;

    void require(size_t count) const
    {
        if (count > size_ - pos_) {
            throw std::runtime_error("truncated MessagePack input");
        }
    }

    uint8_t read_u8()
    {
        require(1);
        return data_[pos_++];
    }

    uint16_t read_u16()
    {
        require(2);
        const uint16_t value = (static_cast<uint16_t>(data_[pos_]) << 8) | static_cast<uint16_t>(data_[pos_ + 1]);
        pos_ += 2;
        return value;
    }

    uint32_t read_u32()
    {
        require(4);
        const uint32_t value = (static_cast<uint32_t>(data_[pos_]) << 24) | (static_cast<uint32_t>(data_[pos_ + 1]) << 16) |
                               (static_cast<uint32_t>(data_[pos_ + 2]) << 8) | static_cast<uint32_t>(data_[pos_ + 3]);
        pos_ += 4;
        return value;
    }

    uint64_t read_u64()
    {
        const uint64_t high = read_u32();
        const uint64_t low = read_u32();
        return (high << 32) | low;
    }

    void skip_bytes(size_t count)
    {
        require(count);
        pos_ += count;
    }

    void skip_items(uint32_t count)
    {
        for (uint32_t index = 0; index < count; ++index) {
            skip();
        }
    }
};

static std::vector<MuninnStreamOption> decode_obs_catalog_payload(const std::vector<uint8_t> &payload)
{
    MsgpackReader reader(payload);
    const uint32_t length = reader.read_array_len();
    if (length < 7) {
        return {};
    }

    reader.skip(); // catalog_id
    reader.skip(); // host_id
    auto stream_ids = reader.read_string_array();
    auto labels = reader.read_string_array();
    auto urls = reader.read_string_array();
    auto states = reader.read_string_array();
    reader.skip(); // updated_at

    std::vector<MuninnStreamOption> options;
    const size_t count = std::min({stream_ids.size(), labels.size(), urls.size(), states.size()});
    options.reserve(count);
    for (size_t index = 0; index < count; ++index) {
        if (stream_ids[index].empty()) {
            continue;
        }
        options.push_back(MuninnStreamOption{
            stream_ids[index],
            labels[index].empty() ? stream_ids[index] : labels[index],
            urls[index],
            states[index],
        });
    }

    std::sort(options.begin(), options.end(), [](const auto &left, const auto &right) {
        return left.label < right.label;
    });
    return options;
}

static std::vector<MuninnStreamOption> read_catalog(const std::string &path)
{
    std::ifstream file(path, std::ios::binary);
    if (!file) {
        return {};
    }

    std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
    if (bytes.empty()) {
        return {};
    }

    try {
        MsgpackReader reader(bytes);
        const uint32_t snapshot_len = reader.read_array_len();
        if (snapshot_len < 3 || reader.read_string() != "cultcache.store.v1") {
            return {};
        }

        const uint32_t catalog_count = reader.read_array_len();
        for (uint32_t index = 0; index < catalog_count; ++index) {
            reader.skip();
        }

        const uint32_t record_count = reader.read_array_len();
        for (uint32_t index = 0; index < record_count; ++index) {
            const uint32_t record_len = reader.read_array_len();
            if (record_len < 4) {
                for (uint32_t item = 0; item < record_len; ++item) {
                    reader.skip();
                }
                continue;
            }

            const std::string key = reader.read_string();
            const std::string schema = reader.read_string();
            reader.skip(); // stored_at
            const auto payload = reader.read_bytes();
            for (uint32_t item = 4; item < record_len; ++item) {
                reader.skip();
            }

            if (key == MuninnObsCatalogKey && (schema == MuninnObsCatalogType || schema == MuninnObsCatalogSchema)) {
                return decode_obs_catalog_payload(payload);
            }
        }
    } catch (const std::exception &error) {
        blog(LOG_WARNING, "Muninn Stream could not read CultCache store %s: %s", path.c_str(), error.what());
    }

    return {};
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

static void populate_stream_list(obs_property_t *property, const std::string &store_path)
{
    obs_property_list_clear(property);
    const auto options = read_catalog(store_path);
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
    std::string store_path = MuninnStorePath;
    if (data) {
        auto *ctx = static_cast<MuninnSource *>(data);
        store_path = ctx->store_path;
    }

    obs_property_t *stream_list = obs_properties_get(props, "stream_id");
    if (stream_list) {
        populate_stream_list(stream_list, store_path);
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
    ctx->store_path = obs_string(settings, "store_path", MuninnStorePath);
    ctx->stream_id = obs_string(settings, "stream_id", "");

    const auto options = read_catalog(ctx->store_path);
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
    obs_data_set_default_string(settings, "store_path", MuninnStorePath);
    obs_data_set_default_string(settings, "stream_id", "");
}

static obs_properties_t *muninn_properties(void *data)
{
    auto *ctx = static_cast<MuninnSource *>(data);
    const auto store_path = ctx ? ctx->store_path : std::string(MuninnStorePath);

    obs_properties_t *props = obs_properties_create();
    obs_properties_add_text(props, "store_path", "Muninn CultCache store", OBS_TEXT_DEFAULT);
    obs_property_t *streams = obs_properties_add_list(props, "stream_id", "Muninn stream", OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
    populate_stream_list(streams, store_path);
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
