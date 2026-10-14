#include "DeviceSession.hpp"
#include "TouchOptions.hpp"

#include "Connection/AdbCommandRunner.hpp"
#include "Utf8Path.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <limits>
#include <sstream>
#include <string_view>
#include <tuple>
#include <utility>
#include <variant>

#include <objidl.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <zlib.h>

namespace UmaAssistant {
namespace {

constexpr std::size_t kMaxFrameBytes = 16 * 1024 * 1024;
constexpr std::size_t kMaxRawFrameBytes = 64 * 1024 * 1024;
constexpr std::size_t kMaxRetainedFrames = 4;
constexpr std::size_t kMaxTextBytes = 64 * 1024;
constexpr std::int32_t kCanonicalWidth = 1080;
constexpr std::int32_t kCanonicalHeight = 1920;
std::atomic<std::uint64_t> g_next_frame_id{1};

[[nodiscard]] std::optional<std::int32_t> parse_axis_max(
    std::string_view section, std::string_view axis)
{
    auto const marker = section.find(axis);
    if (marker == std::string_view::npos) return std::nullopt;
    auto const line_end = section.find('\n', marker);
    auto const line = section.substr(marker,
        line_end == std::string_view::npos ? section.size() - marker : line_end - marker);
    auto const max_marker = line.find("max");
    if (max_marker == std::string_view::npos) return std::nullopt;
    auto value = line.substr(max_marker + 3);
    while (!value.empty() && (value.front() == ' ' || value.front() == '\t')) value.remove_prefix(1);
    std::int32_t result{};
    auto const parsed = std::from_chars(value.data(), value.data() + value.size(), result);
    if (parsed.ec != std::errc{} || result <= 0) return std::nullopt;
    return result;
}

[[nodiscard]] std::string_view trim_view(std::string_view value) noexcept
{
    auto const first = value.find_first_not_of(" \t\r\n");
    if (first == std::string_view::npos) return {};
    auto const last = value.find_last_not_of(" \t\r\n");
    return value.substr(first, last - first + 1);
}

[[nodiscard]] bool command_succeeded(AdbCommandResult const& result) noexcept
{
    return result.started && !result.timed_out && !result.canceled && result.exit_code == 0;
}

[[nodiscard]] bool contains_process_name(
    std::string_view output, std::string_view package_id) noexcept
{
    while (!output.empty())
    {
        auto const line_end = output.find_first_of("\r\n");
        auto line = output.substr(0, line_end);
        output = line_end == std::string_view::npos
            ? std::string_view{}
            : output.substr(line_end + 1);
        while (!line.empty())
        {
            auto const token_end = line.find_first_of(" \t");
            auto const token = line.substr(0, token_end);
            if (token == package_id) return true;
            line = token_end == std::string_view::npos
                ? std::string_view{}
                : line.substr(token_end + 1);
        }
    }
    return false;
}

[[nodiscard]] std::optional<std::string> extract_activity_component(
    std::string_view output)
{
    auto equals_no_activity = [](std::string_view value) noexcept {
        constexpr std::string_view expected{"no activity"};
        if (value.size() < expected.size()) return false;
        auto const lower = [](char character) noexcept {
            if (character >= 'A' && character <= 'Z') return static_cast<char>(character - 'A' + 'a');
            return character;
        };
        for (std::size_t index = 0; index + expected.size() <= value.size(); ++index)
        {
            bool match = true;
            for (std::size_t offset = 0; offset < expected.size(); ++offset)
                if (lower(value[index + offset]) != expected[offset])
                {
                    match = false;
                    break;
                }
            if (match) return true;
        }
        return false;
    };
    std::vector<std::string_view> lines;
    while (!output.empty())
    {
        auto const line_end = output.find_first_of("\r\n");
        auto const line = trim_view(output.substr(0, line_end));
        if (!line.empty()) lines.push_back(line);
        output = line_end == std::string_view::npos
            ? std::string_view{}
            : output.substr(line_end + 1);
    }
    for (auto line_it = lines.rbegin(); line_it != lines.rend(); ++line_it)
    {
        auto const line = *line_it;
        if (equals_no_activity(line))
            continue;
        std::vector<std::string_view> tokens;
        auto remainder = line;
        while (!remainder.empty())
        {
            auto const token_end = remainder.find_first_of(" \t");
            auto const token = remainder.substr(0, token_end);
            if (!token.empty()) tokens.push_back(token);
            remainder = token_end == std::string_view::npos
                ? std::string_view{}
                : remainder.substr(token_end + 1);
        }
        for (auto token_it = tokens.rbegin(); token_it != tokens.rend(); ++token_it)
        {
            if (token_it->find('/') == std::string_view::npos
                || token_it->find('=') != std::string_view::npos)
                continue;
            return std::string{*token_it};
        }
    }
    return std::nullopt;
}

[[nodiscard]] std::int32_t scale_ties_to_even(
    std::int32_t value, std::int32_t reference, std::int32_t maximum)
{
    auto const scaled = static_cast<double>(value)
        * static_cast<double>(maximum) / static_cast<double>((std::max)(1, reference));
    auto const floor_value = std::floor(scaled);
    auto const fraction = scaled - floor_value;
    auto rounded = floor_value;
    if (fraction > 0.5 || (fraction == 0.5
        && std::fmod(floor_value, 2.0) != 0.0))
        rounded += 1.0;
    auto const bounded = (std::clamp)(rounded, 0.0, static_cast<double>(maximum));
    return static_cast<std::int32_t>(bounded);
}

[[nodiscard]] std::pair<std::int32_t, std::int32_t> scale_raw_touch_point(
    std::int32_t x, std::int32_t y,
    std::int32_t screen_width, std::int32_t screen_height,
    std::int32_t max_x, std::int32_t max_y,
    std::int32_t orientation) noexcept
{
    auto const scaled_x = scale_ties_to_even(x, screen_width, max_x);
    auto const scaled_y = scale_ties_to_even(y, screen_height, max_y);
    switch (orientation)
    {
    case 1:
        return {max_x - scale_ties_to_even(y, screen_height, max_x),
                scale_ties_to_even(x, screen_width, max_y)};
    case 2:
        return {max_x - scaled_x, max_y - scaled_y};
    case 3:
        return {scale_ties_to_even(y, screen_height, max_x), max_y
            - scale_ties_to_even(x, screen_width, max_y)};
    default: return {scaled_x, scaled_y};
    }
}

[[nodiscard]] std::uint32_t read_be_u32(std::uint8_t const* bytes) noexcept
{
    return (static_cast<std::uint32_t>(bytes[0]) << 24)
        | (static_cast<std::uint32_t>(bytes[1]) << 16)
        | (static_cast<std::uint32_t>(bytes[2]) << 8)
        | static_cast<std::uint32_t>(bytes[3]);
}

[[nodiscard]] std::uint32_t png_crc32(
    std::uint8_t const* bytes, std::size_t size) noexcept
{
    std::uint32_t crc = 0xffff'ffffu;
    for (std::size_t index = 0; index < size; ++index)
    {
        crc ^= bytes[index];
        for (int bit = 0; bit < 8; ++bit)
            crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xedb8'8320u : crc >> 1;
    }
    return crc ^ 0xffff'ffffu;
}

[[nodiscard]] bool valid_png_chunks(std::vector<std::uint8_t> const& bytes) noexcept
{
    static constexpr std::uint8_t signature[]{137, 80, 78, 71, 13, 10, 26, 10};
    if (bytes.size() < sizeof(signature)
        || !std::equal(std::begin(signature), std::end(signature), bytes.begin()))
        return false;

    std::size_t position = sizeof(signature);
    bool saw_ihdr = false;
    bool saw_idat = false;
    bool saw_iend = false;
    while (position < bytes.size())
    {
        if (bytes.size() - position < 12) return false;
        auto const data_size = static_cast<std::size_t>(read_be_u32(bytes.data() + position));
        if (data_size > bytes.size() - position - 12) return false;
        auto const* type = bytes.data() + position + 4;
        auto const* data = bytes.data() + position + 8;
        auto const expected_crc = read_be_u32(data + data_size);
        auto const actual_crc = png_crc32(type, data_size + 4);
        if (expected_crc != actual_crc) return false;

        bool const ihdr = std::equal(type, type + 4, "IHDR");
        bool const idat = std::equal(type, type + 4, "IDAT");
        bool const iend = std::equal(type, type + 4, "IEND");
        if (!saw_ihdr)
        {
            if (!ihdr || data_size != 13) return false;
            saw_ihdr = true;
        }
        else if (ihdr) return false;
        if (idat) saw_idat = true;
        if (iend)
        {
            if (data_size != 0 || !saw_idat) return false;
            saw_iend = true;
            position += 12;
            break;
        }
        position += data_size + 12;
    }
    return saw_ihdr && saw_idat && saw_iend && position == bytes.size();
}

[[nodiscard]] std::string quote_android_shell_argument(std::string_view value)
{
    std::string quoted{"'"};
    for (auto const character : value)
    {
        if (character == '\'') quoted += "'\\''";
        else quoted.push_back(character);
    }
    quoted.push_back('\'');
    return quoted;
}

[[nodiscard]] std::string escape_android_input_text(std::string_view value)
{
    std::string escaped;
    escaped.reserve(value.size());
    for (unsigned char character : value)
    {
        switch (character)
        {
        case ' ':
            escaped += "%s";
            break;
        case '\\': case '%': case '&': case '|': case ';': case '<': case '>':
        case '(': case ')': case '\'': case '"': case '`': case '$': case '*':
        case '?': case '~': case '#':
            escaped.push_back('\\');
            escaped.push_back(static_cast<char>(character));
            break;
        default:
            escaped.push_back(static_cast<char>(character));
            break;
        }
    }
    return escaped;
}

struct OwnedTouchProcess
{
    std::uint32_t pid{};
    std::string start_token;
};

[[nodiscard]] std::optional<OwnedTouchProcess> parse_owned_pid(std::string_view output)
{
    constexpr std::string_view marker{"__UMA_TOUCH_PID="};
    auto const position = output.find(marker);
    if (position == std::string_view::npos) return std::nullopt;
    auto value = output.substr(position + marker.size());
    auto const line_end = value.find_first_of("\r\n");
    if (line_end == std::string_view::npos) return std::nullopt;
    value = value.substr(0, line_end);
    auto const separator = value.find(':');
    if (separator == std::string_view::npos || separator == 0 || separator + 1 == value.size())
        return std::nullopt;
    auto const pid_value = value.substr(0, separator);
    auto const token = value.substr(separator + 1);
    std::uint32_t pid{};
    auto const parsed = std::from_chars(pid_value.data(), pid_value.data() + pid_value.size(), pid);
    if (token.size() > 32
        || !std::all_of(token.begin(), token.end(), [](char value) {
            return value >= '0' && value <= '9';
        })
        || parsed.ec != std::errc{} || parsed.ptr != pid_value.data() + pid_value.size()
        || pid == 0)
        return std::nullopt;
    return OwnedTouchProcess{pid, std::string{token}};
}

struct TouchHandshake
{
    std::int32_t contacts{};
    std::int32_t max_x{};
    std::int32_t max_y{};
    std::int32_t max_pressure{};
};

[[nodiscard]] std::optional<TouchHandshake> parse_touch_handshake(std::string_view output)
{
    std::size_t line_start = 0;
    while (line_start < output.size())
    {
        auto const line_end = output.find_first_of("\r\n", line_start);
        auto line = output.substr(line_start,
            line_end == std::string_view::npos ? output.size() - line_start : line_end - line_start);
        while (!line.empty() && (line.front() == ' ' || line.front() == '\t')) line.remove_prefix(1);
        if (line.starts_with('^'))
        {
            if (line_end == std::string_view::npos) return std::nullopt;
            line.remove_prefix(1);
            TouchHandshake properties;
            std::istringstream parser{std::string{line}};
            std::string trailing;
            if (parser >> properties.contacts >> properties.max_x >> properties.max_y
                >> properties.max_pressure && !(parser >> trailing)
                && properties.contacts > 0 && properties.max_x > 0 && properties.max_y > 0
                && properties.max_pressure >= 0)
                return properties;
            return std::nullopt;
        }
        if (line_end == std::string_view::npos) break;
        line_start = line_end + 1;
        if (line_start < output.size() && output[line_end] == '\r' && output[line_start] == '\n')
            ++line_start;
    }
    return std::nullopt;
}

[[nodiscard]] bool wait_touch_delay(
    std::chrono::milliseconds duration, std::stop_token cancellation)
{
    auto const deadline = std::chrono::steady_clock::now() + duration;
    while (std::chrono::steady_clock::now() < deadline)
    {
        if (cancellation.stop_requested()) return false;
        auto const remaining = std::chrono::duration_cast<std::chrono::milliseconds>(
            deadline - std::chrono::steady_clock::now());
        std::this_thread::sleep_for((std::min)(remaining, std::chrono::milliseconds{2}));
    }
    return !cancellation.stop_requested();
}

[[nodiscard]] SessionFailure fail(
    ConnectionErrorCode error,
    std::string phase,
    std::string message)
{
    return {static_cast<std::int32_t>(error), std::move(phase), std::move(message)};
}

[[nodiscard]] std::string trim(std::string value)
{
    auto const first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos) return {};
    auto const last = value.find_last_not_of(" \t\r\n");
    return value.substr(first, last - first + 1);
}

[[nodiscard]] std::optional<std::pair<std::int32_t, std::int32_t>> parse_size(
    std::string_view output)
{
    auto const marker = output.rfind("size:");
    if (marker == std::string_view::npos) return std::nullopt;
    auto value = output.substr(marker + 5);
    while (!value.empty() && (value.front() == ' ' || value.front() == '\t')) value.remove_prefix(1);

    std::int32_t width = 0;
    std::int32_t height = 0;
    auto const separator = value.find('x');
    if (separator == std::string_view::npos) return std::nullopt;
    auto const width_text = value.substr(0, separator);
    value.remove_prefix(separator + 1);
    auto const height_end = value.find_first_not_of("0123456789");
    auto const height_text = value.substr(0, height_end);
    auto const width_result = std::from_chars(width_text.data(), width_text.data() + width_text.size(), width);
    auto const height_result = std::from_chars(height_text.data(), height_text.data() + height_text.size(), height);
    if (width_result.ec != std::errc{} || width_result.ptr != width_text.data() + width_text.size()
        || height_result.ec != std::errc{} || height_result.ptr != height_text.data() + height_text.size()
        || width <= 0 || height <= 0 || width > 16384 || height > 16384)
    {
        return std::nullopt;
    }
    return std::pair{width, height};
}

[[nodiscard]] std::optional<std::int32_t> parse_orientation(std::string_view output)
{
    auto parse_number = [](std::string_view value) -> std::optional<std::int32_t> {
        while (!value.empty()
            && (value.front() == ' ' || value.front() == '\t'
                || value.front() == '\r' || value.front() == '\n'))
            value.remove_prefix(1);
        auto const token_end = value.find_first_of(" \t\r\n,;)]}");
        auto const token = value.substr(0, token_end);
        if (token.empty()) return std::nullopt;
        std::int32_t parsed_value = 0;
        auto const parsed = std::from_chars(
            token.data(), token.data() + token.size(), parsed_value);
        if (parsed.ec != std::errc{} || parsed.ptr != token.data() + token.size())
            return std::nullopt;
        return parsed_value;
    };

    for (auto const marker : {"SurfaceOrientation:", "mCurrentOrientation=", "mCurrentOrientation ="})
    {
        auto const position = output.find(marker);
        if (position == std::string_view::npos) continue;
        auto value = output.substr(position + std::char_traits<char>::length(marker));
        auto const orientation = parse_number(value);
        if (!orientation || *orientation < 0 || *orientation > 3)
            return std::nullopt;
        return orientation;
    }
    auto const rotation_position = output.find("rotation=ROTATION_");
    if (rotation_position != std::string_view::npos)
    {
        auto value = output.substr(rotation_position + std::string_view{"rotation=ROTATION_"}.size());
        auto const degrees = parse_number(value);
        if (!degrees) return std::nullopt;
        if (*degrees == 0) return 0;
        if (*degrees == 90) return 1;
        if (*degrees == 180) return 2;
        if (*degrees == 270) return 3;
    }
    return std::nullopt;
}

[[nodiscard]] std::optional<std::int32_t> parse_display_orientation(
    std::string_view output, std::int32_t display_id)
{
    auto const id_text = std::to_string(display_id);
    std::size_t search = 0;
    while ((search = output.find("displayId ", search)) != std::string_view::npos)
    {
        auto const number_begin = search + std::string_view{"displayId "}.size();
        auto const number_end = output.find_first_not_of("0123456789", number_begin);
        auto const number = output.substr(number_begin, number_end - number_begin);
        auto const delimiter = number_end == std::string_view::npos ? '\0' : output[number_end];
        bool const complete_token = delimiter == '\0' || delimiter == ',' || delimiter == ' '
            || delimiter == '\t' || delimiter == '\r' || delimiter == '\n' || delimiter == '}';
        if (complete_token && number == id_text)
        {
            auto const display_begin = output.rfind("DisplayInfo{", search);
            if (display_begin == std::string_view::npos) return std::nullopt;
            auto display_end = output.find("DisplayInfo{", number_end);
            if (display_end == std::string_view::npos) display_end = output.size();
            auto const block = output.substr(display_begin, display_end - display_begin);
            auto const rotation_position = block.find("rotation");
            if (rotation_position == std::string_view::npos) return std::nullopt;
            auto value = block.substr(rotation_position + std::string_view{"rotation"}.size());
            while (!value.empty() && (value.front() == ' ' || value.front() == '\t' || value.front() == '='))
                value.remove_prefix(1);
            auto const token_end = value.find_first_of(" \t\r\n,;)}]");
            auto const token = value.substr(0, token_end);
            std::int32_t orientation = -1;
            auto const parsed = std::from_chars(token.data(), token.data() + token.size(), orientation);
            if (token.empty() || parsed.ec != std::errc{} || parsed.ptr != token.data() + token.size()
                || orientation < 0 || orientation > 3)
                return std::nullopt;
            return orientation;
        }
        search = number_begin;
    }
    return std::nullopt;
}

[[nodiscard]] bool valid_package_id(std::string_view package_id) noexcept
{
    if (package_id.size() < 3 || package_id.size() > 255) return false;
    bool segment_start = true;
    std::size_t segment_count = 0;
    for (char value : package_id)
    {
        if (value == '.')
        {
            if (segment_start) return false;
            segment_start = true;
            ++segment_count;
            continue;
        }
        bool const valid = (value >= 'a' && value <= 'z')
            || (value >= 'A' && value <= 'Z')
            || (value >= '0' && value <= '9')
            || value == '_';
        if (!valid || (segment_start && value >= '0' && value <= '9')) return false;
        segment_start = false;
    }
    return !segment_start && segment_count >= 1;
}

struct DisplayTransform
{
    double scale{};
    double offset_x{};
    double offset_y{};
};

[[nodiscard]] DisplayTransform display_transform(
    std::int32_t width,
    std::int32_t height,
    std::int32_t orientation)
{
    auto const oriented_width = orientation % 2 == 0
        ? kCanonicalWidth : kCanonicalHeight;
    auto const oriented_height = orientation % 2 == 0
        ? kCanonicalHeight : kCanonicalWidth;
    auto const scale = (std::min)(
        static_cast<double>(width) / oriented_width,
        static_cast<double>(height) / oriented_height);
    auto const content_width = oriented_width * scale;
    auto const content_height = oriented_height * scale;
    return {
        .scale = scale,
        .offset_x = (width - content_width) / 2.0,
        .offset_y = (height - content_height) / 2.0,
    };
}

[[nodiscard]] std::optional<std::pair<std::int32_t, std::int32_t>> transform_point(
    std::int32_t x,
    std::int32_t y,
    std::int32_t width,
    std::int32_t height,
    std::int32_t orientation,
    DisplayTransform const& transform)
{
    if (x < 0 || x >= kCanonicalWidth || y < 0 || y >= kCanonicalHeight
        || width <= 0 || height <= 0)
    {
        return std::nullopt;
    }
    double logical_x = 0;
    double logical_y = 0;
    if (orientation == 0)
    {
        logical_x = x;
        logical_y = y;
    }
    else if (orientation == 1)
    {
        logical_x = kCanonicalHeight - 1 - y;
        logical_y = x;
    }
    else if (orientation == 2)
    {
        logical_x = kCanonicalWidth - 1 - x;
        logical_y = kCanonicalHeight - 1 - y;
    }
    else if (orientation == 3)
    {
        logical_x = y;
        logical_y = kCanonicalWidth - 1 - x;
    }
    else return std::nullopt;
    auto const display_x = static_cast<std::int32_t>(
        std::nearbyint(transform.offset_x + logical_x * transform.scale));
    auto const display_y = static_cast<std::int32_t>(
        std::nearbyint(transform.offset_y + logical_y * transform.scale));
    return std::pair{std::clamp(display_x, 0, width - 1),
                     std::clamp(display_y, 0, height - 1)};
}

struct ComScope
{
    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool initialized = SUCCEEDED(result);
    ~ComScope()
    {
        if (initialized) CoUninitialize();
    }
};

struct RawScreenshot
{
    std::int32_t width{};
    std::int32_t height{};
    std::vector<std::uint8_t> rgba;
};

[[nodiscard]] std::optional<RawScreenshot> decode_raw_screenshot(
    std::string_view bytes)
{
    if (bytes.size() < 8 || bytes.size() > kMaxRawFrameBytes) return std::nullopt;
    auto read_le_u32 = [](char const* data) {
        auto const* value = reinterpret_cast<unsigned char const*>(data);
        return static_cast<std::uint32_t>(value[0])
            | (static_cast<std::uint32_t>(value[1]) << 8)
            | (static_cast<std::uint32_t>(value[2]) << 16)
            | (static_cast<std::uint32_t>(value[3]) << 24);
    };
    auto const width = read_le_u32(bytes.data());
    auto const height = read_le_u32(bytes.data() + 4);
    if (width == 0 || height == 0 || width > 16384 || height > 16384)
        return std::nullopt;
    auto const pixels = static_cast<std::uint64_t>(width) * height;
    if (pixels == 0 || pixels > 16ULL * 1024 * 1024) return std::nullopt;
    auto const pixel_bytes = pixels * 4;
    if (bytes.size() < pixel_bytes || bytes.size() - pixel_bytes < 8)
        return std::nullopt;
    auto const payload_offset = bytes.size() - static_cast<std::size_t>(pixel_bytes);
    auto const* rgba = reinterpret_cast<std::uint8_t const*>(bytes.data() + payload_offset);
    if (rgba[pixel_bytes - 1] != 255) return std::nullopt;
    return RawScreenshot{
        .width = static_cast<std::int32_t>(width),
        .height = static_cast<std::int32_t>(height),
        .rgba = std::vector<std::uint8_t>(rgba, rgba + pixel_bytes),
    };
}

[[nodiscard]] std::optional<std::string> gunzip_bounded(std::string_view compressed)
{
    if (compressed.empty() || compressed.size() > kMaxFrameBytes
        || compressed.size() > (std::numeric_limits<uInt>::max)())
        return std::nullopt;
    z_stream stream{};
    stream.next_in = reinterpret_cast<Bytef*>(const_cast<char*>(compressed.data()));
    stream.avail_in = static_cast<uInt>(compressed.size());
    if (inflateInit2(&stream, MAX_WBITS + 16) != Z_OK) return std::nullopt;
    struct InflateGuard
    {
        z_stream& stream;
        ~InflateGuard() { inflateEnd(&stream); }
    } cleanup{stream};
    std::string output;
    std::array<char, 64 * 1024> chunk{};
    int result = Z_OK;
    while (result == Z_OK)
    {
        stream.next_out = reinterpret_cast<Bytef*>(chunk.data());
        stream.avail_out = static_cast<uInt>(chunk.size());
        result = inflate(&stream, Z_NO_FLUSH);
        auto const produced = chunk.size() - stream.avail_out;
        if (produced > kMaxRawFrameBytes - output.size())
        {
            result = Z_MEM_ERROR;
            break;
        }
        output.append(chunk.data(), produced);
    }
    auto const ended = result == Z_STREAM_END;
    if (!ended || stream.avail_in != 0 || output.size() > kMaxRawFrameBytes)
        return std::nullopt;
    return output;
}

[[nodiscard]] std::optional<std::vector<std::uint8_t>> encode_raw_png(
    RawScreenshot const& raw)
{
    if (raw.width <= 0 || raw.height <= 0
        || raw.rgba.size() != static_cast<std::size_t>(raw.width)
            * static_cast<std::size_t>(raw.height) * 4)
        return std::nullopt;
    ComScope com;
    if (FAILED(com.result) && com.result != RPC_E_CHANGED_MODE) return std::nullopt;
    Microsoft::WRL::ComPtr<IWICImagingFactory> factory;
    HRESULT result = ::CoCreateInstance(CLSID_WICImagingFactory, nullptr,
        CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (FAILED(result)) return std::nullopt;
    Microsoft::WRL::ComPtr<IWICBitmapEncoder> encoder;
    result = factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder);
    if (FAILED(result)) return std::nullopt;
    Microsoft::WRL::ComPtr<IStream> stream;
    result = ::CreateStreamOnHGlobal(nullptr, TRUE, &stream);
    if (FAILED(result)) return std::nullopt;
    result = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
    if (FAILED(result)) return std::nullopt;
    Microsoft::WRL::ComPtr<IWICBitmapFrameEncode> frame;
    Microsoft::WRL::ComPtr<IPropertyBag2> properties;
    result = encoder->CreateNewFrame(&frame, &properties);
    if (FAILED(result)) return std::nullopt;
    result = frame->Initialize(properties.Get());
    if (FAILED(result)) return std::nullopt;
    result = frame->SetSize(static_cast<UINT>(raw.width), static_cast<UINT>(raw.height));
    if (FAILED(result)) return std::nullopt;
    WICPixelFormatGUID pixel_format = GUID_WICPixelFormat32bppBGRA;
    result = frame->SetPixelFormat(&pixel_format);
    if (FAILED(result) || pixel_format != GUID_WICPixelFormat32bppBGRA)
        return std::nullopt;
    std::vector<std::uint8_t> bgra(raw.rgba.size());
    for (std::size_t index = 0; index < raw.rgba.size(); index += 4)
    {
        bgra[index] = raw.rgba[index + 2];
        bgra[index + 1] = raw.rgba[index + 1];
        bgra[index + 2] = raw.rgba[index];
        bgra[index + 3] = raw.rgba[index + 3];
    }
    auto const stride = static_cast<UINT>(raw.width) * 4;
    result = frame->WritePixels(static_cast<UINT>(raw.height), stride,
        static_cast<UINT>(bgra.size()), bgra.data());
    if (FAILED(result)) return std::nullopt;
    result = frame->Commit();
    if (FAILED(result)) return std::nullopt;
    result = encoder->Commit();
    if (FAILED(result)) return std::nullopt;
    STATSTG stream_stats{};
    result = stream->Stat(&stream_stats, STATFLAG_NONAME);
    if (FAILED(result) || stream_stats.cbSize.QuadPart == 0
        || stream_stats.cbSize.QuadPart > kMaxFrameBytes)
        return std::nullopt;
    HGLOBAL handle = nullptr;
    result = ::GetHGlobalFromStream(stream.Get(), &handle);
    if (FAILED(result) || handle == nullptr) return std::nullopt;
    auto const allocated_size = ::GlobalSize(handle);
    auto const size = static_cast<std::size_t>(stream_stats.cbSize.QuadPart);
    if (size > allocated_size) return std::nullopt;
    auto const* data = static_cast<std::uint8_t const*>(::GlobalLock(handle));
    if (data == nullptr) return std::nullopt;
    std::vector<std::uint8_t> encoded(data, data + size);
    ::GlobalUnlock(handle);
    return encoded;
}

[[nodiscard]] std::optional<RawScreenshot> decode_raw_payload(
    std::string_view bytes, bool gzip)
{
    std::optional<std::string> decompressed;
    if (gzip)
    {
        decompressed = gunzip_bounded(bytes);
        if (!decompressed) return std::nullopt;
        bytes = *decompressed;
    }
    auto raw = decode_raw_screenshot(bytes);
    if (!raw)
    {
        std::string normalized;
        normalized.reserve(bytes.size());
        for (std::size_t index = 0; index < bytes.size(); ++index)
        {
            if (bytes[index] == '\r' && index + 1 < bytes.size()
                && bytes[index + 1] == '\n')
                continue;
            normalized.push_back(bytes[index]);
        }
        raw = decode_raw_screenshot(normalized);
    }
    if (!raw) return std::nullopt;
    return raw;
}

}

DeviceSession::DeviceSession(
    std::shared_ptr<ConnectionProfile const> profile,
    std::filesystem::path adb_path,
    std::string profile_name,
    ConnectedDevice device,
    std::uint64_t session_generation,
    DeviceSessionOptions options)
    : profile_{std::move(profile)},
      adb_path_{std::move(adb_path)},
      profile_name_{std::move(profile_name)},
      device_{std::move(device)},
      session_generation_{session_generation},
      options_{std::move(options)}
{
}

DeviceSession::~DeviceSession() noexcept
{
    try
    {
        std::lock_guard lock(mutex_);
        (void)release_held_touch(std::stop_token{});
        (void)stop_interactive_touch(false);
    }
    catch (...) {}
}

std::optional<SessionFailure> DeviceSession::update_options(DeviceSessionOptions options)
{
    std::lock_guard lock(mutex_);
    if (!valid_touch_remote_path(options.touch_remote_path)
        || (options.touch_backend != DeviceSessionOptions::TouchBackend::adb
            && options.touch_binary_path.empty())
        || (options.touch_backend == DeviceSessionOptions::TouchBackend::maatouch
            && options.touch_remote_path.find_first_of("'\" \t\r\n") != std::string::npos))
        return fail(ConnectionErrorCode::InvalidArgument, "options", "Touch helper configuration is invalid.");
    bool const display_changed = options_.display_id != options.display_id
        || options_.surface_display_id != options.surface_display_id;
    bool const touch_backend_changed = options_.touch_backend != options.touch_backend
        || options_.touch_binary_path != options.touch_binary_path
        || options_.touch_remote_path != options.touch_remote_path
        || options_.touch_event_id != options.touch_event_id;
    if (display_changed || touch_backend_changed)
    {
        prepared_touch_.reset();
        prepared_touch_device_.reset();
    }
    if ((display_changed || touch_backend_changed)
        && (held_touch_ || (interactive_touch_ && interactive_touch_->pressed)))
    {
        auto const release_failure = release_held_touch(std::stop_token{});
        if (release_failure) return release_failure;
    }
    if (touch_backend_changed && interactive_touch_)
    {
        auto const stop_failure = stop_interactive_touch(false);
        if (stop_failure) return stop_failure;
    }
    if (display_changed || options_.screenshot_method != options.screenshot_method)
        capture_method_.reset();
    if (display_changed)
    {
        if (interactive_touch_)
        {
            auto const stop_failure = stop_interactive_touch(false);
            if (stop_failure) return stop_failure;
        }
        frames_.clear();
        last_geometry_.reset();
        ++geometry_generation_;
        if (geometry_generation_ == 0) ++geometry_generation_;
    }
    options_ = std::move(options);
    return std::nullopt;
}

std::optional<DeviceSession::HeldTouchDevice> DeviceSession::find_touch_device(
    std::stop_token cancellation,
    std::optional<SessionFailure>& failure)
{
    std::string output;
    failure = run_command({"-s", device_.serial, "shell", "getevent", "-lp"},
        std::chrono::seconds(10), cancellation, kMaxTextBytes, output);
    if (failure) return std::nullopt;

    constexpr std::string_view marker{"add device"};
    auto start = output.find(marker);
    while (start != std::string::npos)
    {
        auto const next = output.find(marker, start + marker.size());
        std::string_view const section{output.data() + start,
            (next == std::string::npos ? output.size() : next) - start};
        auto const device_marker = section.find("/dev/input/event");
        auto const path_end = device_marker == std::string_view::npos
            ? std::string_view::npos
            : section.find_first_not_of("0123456789",
                device_marker + std::string_view{"/dev/input/event"}.size());
        auto const max_x = parse_axis_max(section, "ABS_MT_POSITION_X");
        auto const max_y = parse_axis_max(section, "ABS_MT_POSITION_Y");
        if (device_marker != std::string_view::npos && path_end != std::string_view::npos
            && max_x && max_y)
        {
            auto const digits_start = device_marker + std::string_view{"/dev/input/event"}.size();
            if (path_end > digits_start)
            {
                HeldTouchDevice device{
                    .path = std::string{section.substr(device_marker,
                        path_end - device_marker)},
                    .max_x = *max_x,
                    .max_y = *max_y,
                    .has_tool_finger = section.find("BTN_TOOL_FINGER") != std::string_view::npos,
                    .has_touch = section.find("BTN_TOUCH") != std::string_view::npos,
                };
                return device;
            }
        }
        start = next;
    }
    failure = fail(ConnectionErrorCode::DeviceNotReady, "touch_discovery",
        "No writable multi-touch input device was found for a held press.");
    return std::nullopt;
}

std::optional<SessionFailure> DeviceSession::ensure_interactive_touch(
    std::int32_t screen_width,
    std::int32_t screen_height,
    std::int32_t orientation,
    std::stop_token cancellation)
{
    if (options_.touch_backend == DeviceSessionOptions::TouchBackend::adb)
        return std::nullopt;
    if (interactive_touch_ && interactive_touch_->process
        && !interactive_touch_->process->has_exited())
    {
        if (interactive_touch_->screen_width == screen_width
            && interactive_touch_->screen_height == screen_height
            && interactive_touch_->orientation == orientation)
            return std::nullopt;
        if (auto const stop_failure = stop_interactive_touch(true)) return stop_failure;
    }
    if (options_.touch_backend == DeviceSessionOptions::TouchBackend::maatouch
        && options_.display_id.value_or(0) != 0)
        return fail(ConnectionErrorCode::InvalidArgument, "touch_start",
            "MaaTouch currently supports only logical display 0.");
    if (options_.touch_backend == DeviceSessionOptions::TouchBackend::minitouch
        && options_.display_id.value_or(0) != 0 && !options_.touch_event_id)
        return fail(ConnectionErrorCode::InvalidArgument, "touch_start",
            "Minitouch on a secondary display requires a verified input event ID.");
    if (options_.touch_binary_path.empty()
        || !std::filesystem::is_regular_file(options_.touch_binary_path))
        return fail(ConnectionErrorCode::InvalidArgument, "touch_start",
            "The selected touch backend needs an existing local helper binary path.");
    if (!valid_touch_remote_path(options_.touch_remote_path))
        return fail(ConnectionErrorCode::InvalidArgument, "touch_start",
            "The touch helper remote path is invalid.");
    if (options_.touch_backend == DeviceSessionOptions::TouchBackend::maatouch
        && options_.touch_remote_path.find_first_of("'\" ") != std::string::npos)
        return fail(ConnectionErrorCode::InvalidArgument, "touch_start",
            "MaaTouch helper paths cannot contain spaces or quotes.");
    if (cancellation.stop_requested())
        return fail(ConnectionErrorCode::Canceled, "touch_start", "Operation was canceled.");

    if (options_.touch_backend == DeviceSessionOptions::TouchBackend::minitouch
        && options_.touch_event_id)
    {
        std::string devices;
        if (auto const discovery_failure = run_command({"-s", device_.serial, "shell",
                "getevent", "-lp"}, std::chrono::seconds(10), cancellation,
                kMaxTextBytes, devices))
            return discovery_failure;
        auto const path = "/dev/input/event" + *options_.touch_event_id;
        auto position = devices.find(path);
        while (position != std::string::npos)
        {
            auto const end = position + path.size();
            if (end == devices.size() || devices[end] < '0' || devices[end] > '9') break;
            position = devices.find(path, end);
        }
        if (position == std::string::npos)
            return fail(ConnectionErrorCode::InvalidArgument, "touch_display_mapping",
                "The configured minitouch event ID is not present on this device.");
    }

    if (interactive_touch_)
    {
        // A stale local adb shell can leave its remote child alive. Retire its
        // recorded owner before attempting to create a replacement.
        if (auto const stop_failure = stop_interactive_touch(false)) return stop_failure;
    }

    auto const local_path_u8 = options_.touch_binary_path.u8string();
    std::string local_path(reinterpret_cast<char const*>(local_path_u8.data()),
                           local_path_u8.size());
    std::string output;
    if (auto const push_failure = run_command({"-s", device_.serial, "push",
            std::move(local_path), options_.touch_remote_path},
            std::chrono::seconds(30), cancellation, kMaxTextBytes, output))
        return push_failure;
    if (auto const chmod_failure = run_command({"-s", device_.serial, "shell", "chmod", "700",
            options_.touch_remote_path}, std::chrono::seconds(10), cancellation,
            kMaxTextBytes, output))
        return chmod_failure;

    std::string helper_command;
    if (options_.touch_backend == DeviceSessionOptions::TouchBackend::minitouch)
    {
        helper_command = options_.touch_remote_path + " -i";
        if (options_.touch_event_id)
            helper_command += " -d /dev/input/event" + *options_.touch_event_id;
    }
    else
    {
        helper_command = "CLASSPATH=" + options_.touch_remote_path
            + " app_process /data/local/tmp com.shxyke.MaaTouch.App";
    }
    // The wrapper records the exact child PID it created. MaaTouch's `$ 255`
    // greeting is a fixed field; neither backend's protocol greeting is trusted
    // as process ownership.
    std::string const launch_script =
        "touch_pid=; cleanup_touch() { if [ -n \"$touch_pid\" ]; then "
        "kill -TERM \"$touch_pid\" 2>/dev/null; wait \"$touch_pid\" 2>/dev/null; fi; }; "
        "trap cleanup_touch HUP INT TERM; exec 3<&0; " + helper_command
        + " <&3 & touch_pid=$!; touch_start=$(awk '{print $22}' /proc/$touch_pid/stat 2>/dev/null); "
        + "printf '__UMA_TOUCH_PID=%s:%s\\n' \"$touch_pid\" \"$touch_start\" >&2; "
        + "wait \"$touch_pid\"; touch_status=$?; touch_pid=; "
        + "trap - HUP INT TERM; exit \"$touch_status\"";
    auto const launch_script_argument = quote_android_shell_argument(launch_script);
    std::string start_error;
    auto process = start_win32_interactive_session(adb_path_,
        {"-s", device_.serial, "shell", "sh", "-c", launch_script_argument}, start_error);
    if (!process)
        return fail(ConnectionErrorCode::ProcessStartFailed, "touch_start",
            start_error.empty() ? "Could not start the touch helper session." : start_error);

    std::string stdout_data;
    std::string stderr_data;
    std::optional<OwnedTouchProcess> remote_process;
    std::optional<TouchHandshake> handshake;
    auto const deadline = std::chrono::steady_clock::now() + std::chrono::seconds(3);
    auto cancel_drain_deadline = deadline;
    bool cancellation_observed = false;
    while (std::chrono::steady_clock::now() < deadline
        && stdout_data.size() + stderr_data.size() <= kMaxTextBytes)
    {
        auto read_timeout = std::chrono::milliseconds{150};
        auto read_cancellation = cancellation;
        if (cancellation.stop_requested())
        {
            if (!cancellation_observed)
            {
                cancellation_observed = true;
                cancel_drain_deadline = std::chrono::steady_clock::now()
                    + std::chrono::milliseconds{500};
            }
            if (std::chrono::steady_clock::now() >= cancel_drain_deadline) break;
            read_timeout = std::chrono::milliseconds{50};
            read_cancellation = std::stop_token{};
        }
        auto const chunk = process->read(read_timeout, read_cancellation);
        if (chunk.standard_output_truncated || chunk.standard_error_truncated)
        {
            stdout_data.clear();
            stderr_data.clear();
            break;
        }
        stdout_data.append(chunk.standard_output);
        stderr_data.append(chunk.standard_error);
        if (!remote_process) remote_process = parse_owned_pid(stderr_data);
        auto const& handshake_stream = options_.touch_backend
                == DeviceSessionOptions::TouchBackend::minitouch
            ? stderr_data : stdout_data;
        if (!handshake) handshake = parse_touch_handshake(handshake_stream);
        if (remote_process && (handshake || cancellation_observed)) break;
        if (process->has_exited()) break;
    }
    if (!remote_process || !handshake || cancellation_observed || cancellation.stop_requested()
        || process->has_exited() || stdout_data.size() + stderr_data.size() > kMaxTextBytes)
    {
        if (remote_process)
        {
            interactive_touch_ = InteractiveTouch{
                .process = std::move(process),
                .remote_pid = remote_process->pid,
                .remote_start_token = remote_process->start_token,
            };
            (void)stop_interactive_touch(false);
        }
        else process->close();
        if (cancellation.stop_requested())
            return fail(ConnectionErrorCode::Canceled, "touch_start", "Operation was canceled.");
        return fail(ConnectionErrorCode::DeviceNotReady, "touch_handshake",
            "Touch helper did not publish a valid, bounded protocol handshake and owned PID.");
    }

    interactive_touch_ = InteractiveTouch{
        .process = std::move(process),
        .contacts = handshake->contacts,
        .max_x = handshake->max_x,
        .max_y = handshake->max_y,
        .max_pressure = handshake->max_pressure,
        .remote_pid = remote_process->pid,
        .remote_start_token = remote_process->start_token,
        .screen_width = screen_width,
        .screen_height = screen_height,
        .orientation = orientation,
    };
    return std::nullopt;
}

std::optional<SessionFailure> DeviceSession::write_touch_protocol(
    std::string const& bytes, std::stop_token cancellation) noexcept
{
    if (!interactive_touch_ || !interactive_touch_->process
        || interactive_touch_->process->has_exited())
        return fail(ConnectionErrorCode::DeviceNotReady, "touch_input",
            "The interactive touch helper is not running.");
    if (interactive_touch_->process->write(bytes, cancellation)) return std::nullopt;
    return fail(cancellation.stop_requested() ? ConnectionErrorCode::Canceled
                                               : ConnectionErrorCode::CommandFailed,
        "touch_input", "The interactive touch helper rejected the input protocol write.");
}

std::optional<SessionFailure> DeviceSession::stop_interactive_touch(bool release_first) noexcept
{
    if (!interactive_touch_) return std::nullopt;
    auto& touch = *interactive_touch_;
    std::optional<SessionFailure> failure;
    if (release_first && touch.pressed && touch.process && !touch.process->has_exited())
    {
        failure = write_touch_protocol("u 0\nc\nr\n", std::stop_token{});
        if (!failure)
        {
            touch.pressed = false;
            // The helper has no protocol acknowledgement. Give its input
            // thread a short bounded window to consume the reset before we
            // close the shell channel and terminate the owned process.
            (void)wait_touch_delay(std::chrono::milliseconds{50}, std::stop_token{});
        }
    }
    if (touch.process) touch.process->close();
    auto const remote_pid = touch.remote_pid;
    if (remote_pid != 0)
    {
        std::string output;
        auto const pid = std::to_string(remote_pid);
        auto const start_token = touch.remote_start_token;
        std::string stop_script =
                "current=$(awk '{print $22}' /proc/" + pid + "/stat 2>/dev/null); "
                "if [ \"$current\" != \"" + start_token + "\" ]; then echo stopped; exit 0; fi; "
                "kill -TERM " + pid + " 2>/dev/null || exit 1; "
                "for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do "
                "current=$(awk '{print $22}' /proc/" + pid + "/stat 2>/dev/null); "
                "if [ \"$current\" != \"" + start_token + "\" ]; then echo stopped; exit 0; fi; "
                "sleep 0.1; done; "
                "current=$(awk '{print $22}' /proc/" + pid + "/stat 2>/dev/null); "
                "if [ \"$current\" = \"" + start_token + "\" ]; then kill -KILL " + pid + " 2>/dev/null; fi; "
                "for i in 1 2 3 4 5 6 7 8 9 10; do "
                "current=$(awk '{print $22}' /proc/" + pid + "/stat 2>/dev/null); "
                "if [ \"$current\" != \"" + start_token + "\" ]; then echo stopped; exit 0; fi; "
                "sleep 0.1; done; exit 1";
        auto const stop_script_argument = quote_android_shell_argument(stop_script);
        auto const stop_failure = run_command({"-s", device_.serial, "shell", "sh", "-c",
                stop_script_argument}, std::chrono::seconds(5), std::stop_token{},
            kMaxTextBytes, output);
        if (stop_failure || trim(output) != "stopped")
        {
            if (!failure)
                failure = stop_failure.value_or(fail(ConnectionErrorCode::CommandFailed,
                    "touch_stop", "The owned remote touch helper is still running."));
            // Keep the owner token and PID so a later cleanup can retry. Do not
            // forget an unconfirmed process or risk killing a PID reused later.
            return failure;
        }
    }
    touch.pressed = false;
    interactive_touch_frame_.reset();
    interactive_touch_.reset();
    return failure;
}

std::pair<std::int32_t, std::int32_t> DeviceSession::scale_touch_point(
    std::int32_t x, std::int32_t y,
    std::int32_t screen_width, std::int32_t screen_height,
    std::int32_t orientation) const noexcept
{
    if (!interactive_touch_) return {x, y};
    auto const& touch = *interactive_touch_;
    if (options_.touch_backend == DeviceSessionOptions::TouchBackend::minitouch)
        return scale_raw_touch_point(x, y, screen_width, screen_height,
                                     touch.max_x, touch.max_y, orientation);
    // MaaTouch dispatches Android MotionEvents in logical display coordinates.
    return {scale_ties_to_even(x, screen_width, touch.max_x),
            scale_ties_to_even(y, screen_height, touch.max_y)};
}

std::optional<SessionFailure> DeviceSession::release_held_touch(
    std::stop_token cancellation) noexcept
{
    if (interactive_touch_ && interactive_touch_->pressed)
    {
        auto failure = write_touch_protocol("u 0\nc\nr\n", std::stop_token{});
        if (!failure)
        {
            interactive_touch_->pressed = false;
            interactive_touch_frame_.reset();
            (void)wait_touch_delay(std::chrono::milliseconds{50}, std::stop_token{});
        }
        else
        {
            auto const stop_failure = stop_interactive_touch(false);
            if (stop_failure) return stop_failure;
        }
        return failure;
    }
    if (!held_touch_) return std::nullopt;
    try
    {
        auto const& device = *held_touch_;
        std::string command = "set -e; sendevent " + device.path + " 3 47 0; sendevent "
            + device.path + " 3 57 -1;";
        if (device.has_tool_finger)
            command += " sendevent " + device.path + " 1 325 0;";
        if (device.has_touch)
            command += " sendevent " + device.path + " 1 330 0;";
        command += " sendevent " + device.path + " 0 0 0";
        std::string output;
        auto const failure = run_command({"-s", device_.serial, "shell", "sh", "-c",
            quote_android_shell_argument(command)},
            std::chrono::seconds(10), cancellation, kMaxTextBytes, output);
        if (!failure)
        {
            held_touch_.reset();
            held_touch_frame_.reset();
        }
        return failure;
    }
    catch (...)
    {
        return fail(ConnectionErrorCode::CommandFailed, "touch_release",
            "Could not release the held touch safely.");
    }
}

DeviceOperationResult DeviceSession::verify_game(
    std::string const& package_id,
    std::stop_token cancellation)
{
    std::lock_guard lock(mutex_);
    if (!valid_package_id(package_id))
    {
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "verify_game", "Package ID is invalid.")};
    }

    std::string output;
    auto const command_failure = run_command(
        {"-s", device_.serial, "shell", "pm", "path", package_id},
        std::chrono::seconds(15), cancellation, kMaxTextBytes, output);
    if (command_failure)
        return {.failure = command_failure};
    if (output.find("package:") == std::string::npos)
    {
        return {.failure = fail(ConnectionErrorCode::TargetGameNotInstalled,
                                "verify_game", "The configured game package is not installed.")};
    }
    return {.package_id = package_id};
}

DeviceOperationResult DeviceSession::capture(std::stop_token cancellation, bool retain_pixels)
{
    std::lock_guard lock(mutex_);
    std::optional<SessionFailure> geometry_failure;
    auto const before = current_geometry(cancellation, geometry_failure);
    if (geometry_failure) return {.failure = std::move(geometry_failure)};
    if (!before)
        return {.failure = fail(ConnectionErrorCode::InvalidDeviceResponse,
                                "capture", "Could not determine display geometry.")};
    struct CapturedImage
    {
        CaptureMethod method{};
        std::vector<std::uint8_t> png;
        std::chrono::steady_clock::duration duration{};
        std::pair<std::int32_t, std::int32_t> dimensions;
        std::shared_ptr<const Vision::GrayImage> pixels;
    };
    auto attempt = [&](CaptureMethod method) -> std::variant<CapturedImage, SessionFailure> {
        std::vector<std::string> arguments;
        bool raw = false;
        bool gzip = false;
        std::string display_id = options_.surface_display_id.value_or(std::string{});
        switch (method)
        {
        case CaptureMethod::exec_raw:
            arguments = {"-s", device_.serial, "exec-out", "screencap"};
            if (options_.surface_display_id)
            {
                arguments.push_back("-d");
                arguments.push_back(display_id);
            }
            raw = true;
            break;
        case CaptureMethod::shell_gzip_raw:
            arguments = {"-s", device_.serial, "shell", "sh", "-c",
                quote_android_shell_argument(options_.surface_display_id
                    ? "screencap -d " + display_id + " | gzip -1"
                    : "screencap | gzip -1")};
            raw = true;
            gzip = true;
            break;
        case CaptureMethod::exec_png:
            arguments = {"-s", device_.serial, "exec-out", "screencap"};
            if (options_.surface_display_id)
            {
                arguments.push_back("-d");
                arguments.push_back(display_id);
            }
            arguments.push_back("-p");
            break;
        case CaptureMethod::shell_png:
            arguments = {"-s", device_.serial, "shell", "screencap"};
            if (options_.surface_display_id)
            {
                arguments.push_back("-d");
                arguments.push_back(display_id);
            }
            arguments.push_back("-p");
            break;
        }
        std::string output;
        auto const started = std::chrono::steady_clock::now();
        auto const command_failure = run_command(
            std::move(arguments), std::chrono::seconds(20), cancellation,
            raw ? kMaxRawFrameBytes : kMaxFrameBytes, output);
        auto duration = std::chrono::steady_clock::now() - started;
        if (command_failure) return *command_failure;
        if (output.empty())
            return fail(ConnectionErrorCode::InvalidDeviceResponse,
                        "capture", "Screenshot output is empty.");
        std::vector<std::uint8_t> png;
        if (raw)
        {
            auto decoded = decode_raw_payload(output, gzip);
            duration = std::chrono::steady_clock::now() - started;
            if (!decoded)
                return fail(ConnectionErrorCode::InvalidDeviceResponse,
                            "capture", "ADB returned an invalid raw screenshot frame.");
            if (decoded->width != before->width || decoded->height != before->height)
                return fail(ConnectionErrorCode::DeviceNotReady,
                            "capture", "Screenshot dimensions do not match the display geometry.");
            auto converted = encode_raw_png(*decoded);
            if (!converted)
                return fail(ConnectionErrorCode::InvalidDeviceResponse,
                            "capture", "Could not encode the raw screenshot frame.");
            png = std::move(*converted);
        }
        else
        {
            if (output.size() > kMaxFrameBytes)
                return fail(ConnectionErrorCode::InvalidDeviceResponse,
                            "capture", "Screenshot output exceeds 16 MiB.");
            png.resize(output.size());
            std::transform(output.begin(), output.end(), png.begin(), [](char value) {
                return static_cast<std::uint8_t>(static_cast<unsigned char>(value));
            });
        }
        auto pixels = retain_pixels ? std::make_shared<Vision::GrayImage>() : nullptr;
        auto const dimensions = decode_png(png, pixels.get());
        if (!dimensions)
            return fail(ConnectionErrorCode::InvalidDeviceResponse,
                        "capture", "ADB returned an invalid or unsupported screenshot frame.");
        if (dimensions->first != before->width || dimensions->second != before->height)
            return fail(ConnectionErrorCode::DeviceNotReady,
                        "capture", "Screenshot dimensions do not match the display geometry.");
        return CapturedImage{method, std::move(png), duration, *dimensions, std::move(pixels)};
    };

    std::optional<CapturedImage> captured;
    std::optional<SessionFailure> last_failure;
    if (capture_method_)
    {
        auto cached = attempt(*capture_method_);
        if (auto* image = std::get_if<CapturedImage>(&cached))
            captured = std::move(*image);
        else
        {
            last_failure = std::get<SessionFailure>(std::move(cached));
            capture_method_.reset();
        }
    }
    if (!captured)
    {
        std::vector<CaptureMethod> methods;
        switch (options_.screenshot_method)
        {
        case DeviceSessionOptions::ScreenshotMethod::automatic:
            methods = {CaptureMethod::exec_raw, CaptureMethod::shell_gzip_raw,
                       CaptureMethod::exec_png, CaptureMethod::shell_png};
            break;
        case DeviceSessionOptions::ScreenshotMethod::raw:
            methods = {CaptureMethod::exec_raw};
            break;
        case DeviceSessionOptions::ScreenshotMethod::gzip:
            methods = {CaptureMethod::shell_gzip_raw};
            break;
        case DeviceSessionOptions::ScreenshotMethod::png:
            methods = {CaptureMethod::exec_png};
            break;
        case DeviceSessionOptions::ScreenshotMethod::shell_png:
            methods = {CaptureMethod::shell_png};
            break;
        }
        for (auto const method : methods)
        {
            auto candidate = attempt(method);
            if (auto* image = std::get_if<CapturedImage>(&candidate))
            {
                if (!captured || image->duration < captured->duration)
                    captured = std::move(*image);
            }
            else
            {
                last_failure = std::get<SessionFailure>(std::move(candidate));
            }
        }
    }
    if (!captured)
        return {.failure = last_failure.value_or(fail(
            ConnectionErrorCode::InvalidDeviceResponse,
            "capture", "No supported ADB screenshot method succeeded."))};
    capture_method_ = captured->method;
    auto bytes = std::move(captured->png);
    // The selected immutable PNG was fully validated in attempt(). Retain its
    // dimensions and optional pixels instead of decoding the same bytes again.
    auto const dimensions = captured->dimensions;
    auto const after = current_geometry(cancellation, geometry_failure);
    if (geometry_failure) return {.failure = std::move(geometry_failure)};
    if (!after || *before != *after || dimensions.first != after->width
        || dimensions.second != after->height)
    {
        return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                "capture", "Display geometry changed during screenshot capture.")};
    }

    if (frames_.size() >= kMaxRetainedFrames)
    {
        return {.failure = fail(ConnectionErrorCode::Busy,
                                "capture", "Release a retained screenshot before capturing another.")};
    }

    auto frame_id = g_next_frame_id.fetch_add(1, std::memory_order_relaxed);
    if (frame_id == 0) frame_id = g_next_frame_id.fetch_add(1, std::memory_order_relaxed);
    auto const transform = display_transform(after->width, after->height, after->orientation);
    DeviceFrame frame{
        .metadata = {
            .id = frame_id,
            .session_generation = session_generation_,
            .geometry_generation = geometry_generation_,
            .width = after->width,
            .height = after->height,
            .orientation = after->orientation,
            .byte_count = bytes.size(),
            .display_scale = transform.scale,
            .display_offset_x = transform.offset_x,
            .display_offset_y = transform.offset_y,
        },
        .png = std::move(bytes),
        .pixels = std::move(captured->pixels),
    };
    auto [stored, inserted] = frames_.emplace(frame_id, std::move(frame));
    if (!inserted)
        return {.failure = fail(ConnectionErrorCode::CommandFailed,
                                "capture", "Could not retain screenshot frame.")};
    return {.frame = stored->second.metadata};
}

DeviceOperationResult DeviceSession::tap(
    std::uint64_t frame_id,
    std::int32_t canonical_x,
    std::int32_t canonical_y,
    std::stop_token cancellation)
{
    std::lock_guard lock(mutex_);
    if (held_touch_ || (interactive_touch_ && interactive_touch_->pressed))
        return {.failure = fail(ConnectionErrorCode::Busy,
                                "tap", "A held touch must be released before another input.")};
    auto frame_it = frames_.find(frame_id);
    if (frame_it == frames_.end()
        || frame_it->second.metadata.session_generation != session_generation_
        || frame_it->second.metadata.geometry_generation != geometry_generation_)
    {
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "tap", "Screenshot frame is no longer current.")};
    }
    auto const source_frame = frame_it->second.metadata;
    DeviceOperationResult::Point const request{
        .canonical_x = canonical_x,
        .canonical_y = canonical_y,
    };

    std::optional<SessionFailure> geometry_failure;
    auto const geometry = current_geometry(cancellation, geometry_failure);
    if (geometry_failure)
        return {.failure = std::move(geometry_failure), .source_frame = source_frame,
                .requested_first_point = request};
    if (!geometry || geometry->width != frame_it->second.metadata.width
        || geometry->height != frame_it->second.metadata.height
        || geometry->orientation != frame_it->second.metadata.orientation
        || frame_it->second.metadata.geometry_generation != geometry_generation_)
    {
        return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                "tap", "Device screen geometry changed after capture."),
                .source_frame = source_frame, .requested_first_point = request};
    }
    auto const point = transform_point(canonical_x, canonical_y,
                                      frame_it->second.metadata.width,
                                      frame_it->second.metadata.height,
                                      frame_it->second.metadata.orientation,
        DisplayTransform{source_frame.display_scale, source_frame.display_offset_x,
                         source_frame.display_offset_y});
    if (!point)
    {
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "tap", "Tap coordinates are outside the canonical screen."),
                .source_frame = source_frame, .requested_first_point = request};
    }
    auto input_point = *point;
    if (options_.touch_backend != DeviceSessionOptions::TouchBackend::adb)
    {
        if (auto const start_failure = ensure_interactive_touch(
                source_frame.width, source_frame.height, source_frame.orientation, cancellation))
            return {.failure = start_failure, .source_frame = source_frame,
                    .requested_first_point = request};
        input_point = scale_touch_point(point->first, point->second, source_frame.width,
                                        source_frame.height, source_frame.orientation);
        interactive_touch_->pressed = true;
        std::string const down = "d 0 " + std::to_string(input_point.first) + " "
            + std::to_string(input_point.second) + " "
            + std::to_string(interactive_touch_->max_pressure) + "\nc\nw 50\n";
        auto input_failure = write_touch_protocol(down, cancellation);
        if (!input_failure && !wait_touch_delay(std::chrono::milliseconds{50}, cancellation))
            input_failure = fail(ConnectionErrorCode::Canceled, "tap", "Tap was canceled.");
        if (!input_failure)
            input_failure = write_touch_protocol("u 0\nc\nw 50\n", cancellation);
        if (!input_failure && !wait_touch_delay(std::chrono::milliseconds{50}, cancellation))
            input_failure = fail(ConnectionErrorCode::Canceled, "tap", "Tap was canceled.");
        if (input_failure)
        {
            auto const cleanup_failure = stop_interactive_touch(true);
            return {.failure = cleanup_failure ? cleanup_failure : input_failure,
                    .source_frame = source_frame, .requested_first_point = request};
        }
        interactive_touch_->pressed = false;
    }
    else
    {
    std::string output;
    auto const command_failure = run_command(
        options_.display_id
            ? std::vector<std::string>{"-s", device_.serial, "shell", "input", "-d",
                std::to_string(*options_.display_id), "tap",
                std::to_string(point->first), std::to_string(point->second)}
            : std::vector<std::string>{"-s", device_.serial, "shell", "input", "tap",
         std::to_string(point->first), std::to_string(point->second)},
        std::chrono::seconds(15), cancellation, kMaxTextBytes, output);
    if (command_failure)
        return {.failure = command_failure, .source_frame = source_frame,
                .requested_first_point = request};
    }
    return {.source_frame = source_frame, .requested_first_point = request,
        .first_point = DeviceOperationResult::Point{
        .canonical_x = canonical_x,
        .canonical_y = canonical_y,
        .device_x = input_point.first,
        .device_y = input_point.second,
    }};
}

DeviceOperationResult DeviceSession::key_event(
    std::uint64_t frame_id,
    std::string const& key_code,
    std::stop_token cancellation)
{
    std::lock_guard lock(mutex_);
    if (key_code.empty() || key_code.size() > 128)
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "key_event", "Key code must contain between 1 and 128 bytes.")};
    if (held_touch_ || (interactive_touch_ && interactive_touch_->pressed))
        return {.failure = fail(ConnectionErrorCode::Busy,
                                "key_event", "A held touch must be released before another input.")};
    auto frame_it = frames_.find(frame_id);
    if (frame_it == frames_.end()
        || frame_it->second.metadata.session_generation != session_generation_
        || frame_it->second.metadata.geometry_generation != geometry_generation_)
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "key_event", "Screenshot frame is no longer current.")};

    auto const source_frame = frame_it->second.metadata;
    std::optional<SessionFailure> geometry_failure;
    auto const geometry = current_geometry(cancellation, geometry_failure);
    if (geometry_failure)
        return {.failure = std::move(geometry_failure), .source_frame = source_frame};
    if (!geometry || geometry->width != source_frame.width
        || geometry->height != source_frame.height
        || geometry->orientation != source_frame.orientation
        || source_frame.geometry_generation != geometry_generation_)
        return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                "key_event", "Device screen geometry changed after capture."),
                .source_frame = source_frame};

    std::string output;
    auto const command_failure = run_command(
        options_.display_id
            ? std::vector<std::string>{"-s", device_.serial, "shell", "input", "-d",
                std::to_string(*options_.display_id), "keyevent",
                quote_android_shell_argument(key_code)}
            : std::vector<std::string>{"-s", device_.serial, "shell", "input", "keyevent",
                quote_android_shell_argument(key_code)},
        std::chrono::seconds(15), cancellation, kMaxTextBytes, output);
    if (command_failure)
        return {.failure = command_failure, .source_frame = source_frame};
    return {.source_frame = source_frame};
}

DeviceOperationResult DeviceSession::input_text(
    std::uint64_t frame_id,
    std::string const& text,
    std::stop_token cancellation)
{
    std::lock_guard lock(mutex_);
    if (text.empty() || text.size() > kMaxTextBytes)
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "input_text", "Input text must contain between 1 and 65536 bytes.")};
    if (held_touch_ || (interactive_touch_ && interactive_touch_->pressed))
        return {.failure = fail(ConnectionErrorCode::Busy,
                                "input_text", "A held touch must be released before another input.")};
    auto frame_it = frames_.find(frame_id);
    if (frame_it == frames_.end()
        || frame_it->second.metadata.session_generation != session_generation_
        || frame_it->second.metadata.geometry_generation != geometry_generation_)
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "input_text", "Screenshot frame is no longer current.")};

    auto const source_frame = frame_it->second.metadata;
    std::optional<SessionFailure> geometry_failure;
    auto const geometry = current_geometry(cancellation, geometry_failure);
    if (geometry_failure)
        return {.failure = std::move(geometry_failure), .source_frame = source_frame};
    if (!geometry || geometry->width != source_frame.width
        || geometry->height != source_frame.height
        || geometry->orientation != source_frame.orientation
        || source_frame.geometry_generation != geometry_generation_)
        return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                "input_text", "Device screen geometry changed after capture."),
                .source_frame = source_frame};

    std::string output;
    auto const command_failure = run_command(
        {"-s", device_.serial, "shell", "input", "text", escape_android_input_text(text)},
        std::chrono::seconds(15), cancellation, kMaxTextBytes, output);
    if (command_failure)
        return {.failure = command_failure, .source_frame = source_frame};
    return {.source_frame = source_frame};
}

DeviceOperationResult DeviceSession::package_operation(
    PackageOperationKind operation,
    std::string const& package_id,
    std::string const& activity,
    std::stop_token cancellation)
{
    std::lock_guard lock(mutex_);
    if (package_id.empty() || package_id.size() > 512
        || package_id.find_first_of(" \t\r\n") != std::string::npos)
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "package_operation", "Package name is invalid.")};
    if (operation == PackageOperationKind::start_activity
        && (activity.empty() || activity.size() > 1024
            || activity.find_first_of(" \t\r\n") != std::string::npos))
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "package_operation", "Activity name is invalid.")};
    if ((operation == PackageOperationKind::start_package
         || operation == PackageOperationKind::start_activity
         || operation == PackageOperationKind::stop_package)
        && (held_touch_ || (interactive_touch_ && interactive_touch_->pressed)))
        return {.failure = fail(ConnectionErrorCode::Busy,
                                "package_operation",
                                "A held touch must be released before changing the running application.")};

    DeviceOperationResult::PackageCommand command;
    command.operation = static_cast<std::int32_t>(operation);
    auto run_package_command = [&](std::vector<std::string> arguments) {
        auto const operation_value = command.operation;
        command = DeviceOperationResult::PackageCommand{};
        command.operation = operation_value;
        std::string output;
        AdbCommandResult result;
        auto failure = run_command(
            std::move(arguments), std::chrono::seconds(15), cancellation,
            operation == PackageOperationKind::query_running ? kMaxTextBytes : 8192,
            output, false, &result);
        command.exit_code = result.exit_code;
        command.started = result.started;
        command.timed_out = result.timed_out;
        command.canceled = result.canceled;
        command.standard_output = std::move(result.standard_output);
        command.standard_error = std::move(result.standard_error);
        if (failure)
        {
            command.error_code = failure->error_code;
            command.error_message = std::move(failure->message);
        }
        return std::pair{std::move(failure), std::move(output)};
    };
    auto canceled = [](std::optional<SessionFailure> const& failure) {
        return failure && failure->error_code == static_cast<std::int32_t>(ConnectionErrorCode::Canceled);
    };

    if (operation == PackageOperationKind::start_package)
    {
        auto [resolve_failure, resolved_output] = run_package_command(
            {"-s", device_.serial, "shell", "cmd", "package", "resolve-activity",
             "--brief", package_id});
        if (canceled(resolve_failure)) return {.failure = std::move(resolve_failure)};
        std::optional<std::string> component;
        if (!resolve_failure && command_succeeded(AdbCommandResult{
                .exit_code = command.exit_code,
                .standard_output = command.standard_output,
                .standard_error = command.standard_error,
                .started = command.started,
                .timed_out = command.timed_out,
                .canceled = command.canceled}))
            component = extract_activity_component(resolved_output);

        if (component)
        {
            std::tie(resolve_failure, resolved_output) = run_package_command(
                {"-s", device_.serial, "shell", "am", "start", "-n", *component});
            if (canceled(resolve_failure)) return {.failure = std::move(resolve_failure)};
        }
        else
        {
            std::tie(resolve_failure, resolved_output) = run_package_command(
                {"-s", device_.serial, "shell", "am", "start", "-a",
                 "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER",
                 "-p", package_id});
            if (canceled(resolve_failure)) return {.failure = std::move(resolve_failure)};
        }
    }
    else if (operation == PackageOperationKind::start_activity)
    {
        auto component = activity.find('/') == std::string::npos
            ? package_id + "/" + activity
            : activity;
        auto [failure, output] = run_package_command(
            {"-s", device_.serial, "shell", "am", "start", "-n", std::move(component)});
        if (canceled(failure)) return {.failure = std::move(failure)};
    }
    else if (operation == PackageOperationKind::stop_package)
    {
        auto [failure, output] = run_package_command(
            {"-s", device_.serial, "shell", "am", "force-stop", package_id});
        if (canceled(failure)) return {.failure = std::move(failure)};
    }
    else if (operation == PackageOperationKind::query_running)
    {
        auto [pid_failure, pid_output] = run_package_command(
            {"-s", device_.serial, "shell", "pidof", package_id});
        if (canceled(pid_failure)) return {.failure = std::move(pid_failure)};
        if (!pid_failure && command_succeeded(AdbCommandResult{
                .exit_code = command.exit_code,
                .standard_output = command.standard_output,
                .standard_error = command.standard_error,
                .started = command.started,
                .timed_out = command.timed_out,
                .canceled = command.canceled}))
        {
            command.process_running = !trim_view(pid_output).empty();
        }
        else
        {
            auto [ps_failure, ps_output] = run_package_command(
                {"-s", device_.serial, "shell", "ps", "-A"});
            if (canceled(ps_failure)) return {.failure = std::move(ps_failure)};
            command.process_running = !ps_failure
                && command_succeeded(AdbCommandResult{
                    .exit_code = command.exit_code,
                    .standard_output = command.standard_output,
                    .standard_error = command.standard_error,
                    .started = command.started,
                    .timed_out = command.timed_out,
                    .canceled = command.canceled})
                && contains_process_name(ps_output, package_id);
        }
        command.standard_output.clear();
        command.standard_error.clear();
    }
    else
    {
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "package_operation", "Unknown package operation.")};
    }

    if (command.standard_output.size() > 8192) command.standard_output.resize(8192);
    if (command.standard_error.size() > 8192) command.standard_error.resize(8192);
    DeviceOperationResult result;
    result.package_id = package_id;
    result.package_command = std::move(command);
    return result;
}

DeviceOperationResult DeviceSession::swipe(
    std::uint64_t frame_id,
    std::int32_t x1,
    std::int32_t y1,
    std::int32_t x2,
    std::int32_t y2,
    std::int32_t duration_ms,
    bool extra_swipe,
    std::stop_token cancellation)
{
    std::lock_guard lock(mutex_);
    if (held_touch_ || (interactive_touch_ && interactive_touch_->pressed))
        return {.failure = fail(ConnectionErrorCode::Busy,
                                "swipe", "A held touch must be released before another input.")};
    auto frame_it = frames_.find(frame_id);
    if (frame_it == frames_.end()
        || frame_it->second.metadata.session_generation != session_generation_
        || frame_it->second.metadata.geometry_generation != geometry_generation_)
    {
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "swipe", "Screenshot frame is no longer current.")};
    }
    auto const source_frame = frame_it->second.metadata;
    DeviceOperationResult::Point const first_request{
        .canonical_x = x1, .canonical_y = y1,
    };
    DeviceOperationResult::Point const second_request{
        .canonical_x = x2, .canonical_y = y2,
    };
    if (duration_ms < 0 || duration_ms > 60000)
    {
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "swipe", "Swipe duration must be between 0 and 60000 ms."),
                .source_frame = source_frame, .requested_first_point = first_request,
                .requested_second_point = second_request};
    }

    std::optional<SessionFailure> geometry_failure;
    auto const geometry = current_geometry(cancellation, geometry_failure);
    if (geometry_failure)
        return {.failure = std::move(geometry_failure), .source_frame = source_frame,
                .requested_first_point = first_request,
                .requested_second_point = second_request};
    if (!geometry || geometry->width != frame_it->second.metadata.width
        || geometry->height != frame_it->second.metadata.height
        || geometry->orientation != frame_it->second.metadata.orientation
        || frame_it->second.metadata.geometry_generation != geometry_generation_)
    {
        return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                "swipe", "Device screen geometry changed after capture."),
                .source_frame = source_frame, .requested_first_point = first_request,
                .requested_second_point = second_request};
    }
    auto const first = transform_point(
        x1, y1, frame_it->second.metadata.width, frame_it->second.metadata.height,
        frame_it->second.metadata.orientation,
        DisplayTransform{source_frame.display_scale, source_frame.display_offset_x,
                         source_frame.display_offset_y});
    auto const second = transform_point(
        x2, y2, frame_it->second.metadata.width, frame_it->second.metadata.height,
        frame_it->second.metadata.orientation,
        DisplayTransform{source_frame.display_scale, source_frame.display_offset_x,
                         source_frame.display_offset_y});
    if (!first || !second)
    {
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "swipe", "Swipe coordinates are outside the canonical screen."),
                .source_frame = source_frame, .requested_first_point = first_request,
                .requested_second_point = second_request};
    }
    auto const scaled_duration = duration_ms == 0
        ? 0
        : (std::max)(1, static_cast<std::int32_t>(
            static_cast<double>(duration_ms) * options_.swipe_duration_multiplier));
    std::string output;
    if (options_.touch_backend != DeviceSessionOptions::TouchBackend::adb)
    {
        if (auto const start_failure = ensure_interactive_touch(
                source_frame.width, source_frame.height, source_frame.orientation, cancellation))
            return {.failure = start_failure, .source_frame = source_frame,
                    .requested_first_point = first_request,
                    .requested_second_point = second_request};
        auto const from = scale_touch_point(first->first, first->second,
            source_frame.width, source_frame.height, source_frame.orientation);
        auto const to = scale_touch_point(second->first, second->second,
            source_frame.width, source_frame.height, source_frame.orientation);
        auto const touch_duration = duration_ms > 0 ? duration_ms : 200;
        auto const steps = (std::max)(1, static_cast<std::int32_t>(std::ceil(
            static_cast<double>(touch_duration) / 2.0)));
        std::string protocol = "d 0 " + std::to_string(from.first) + " "
            + std::to_string(from.second) + " "
            + std::to_string(interactive_touch_->max_pressure) + "\nc\nw 50\n";
        interactive_touch_->pressed = true;
        auto input_failure = write_touch_protocol(protocol, cancellation);
        if (!input_failure && !wait_touch_delay(std::chrono::milliseconds{50}, cancellation))
            input_failure = fail(ConnectionErrorCode::Canceled, "swipe", "Swipe was canceled.");
        protocol.clear();
        for (std::int32_t step = 1; step <= steps; ++step)
        {
            if (input_failure) break;
            auto const ratio = static_cast<double>(step) / steps;
            auto const x = static_cast<std::int32_t>(std::nearbyint(
                from.first + (to.first - from.first) * ratio));
            auto const y = static_cast<std::int32_t>(std::nearbyint(
                from.second + (to.second - from.second) * ratio));
            protocol = "m 0 " + std::to_string(x) + " " + std::to_string(y) + " "
                + std::to_string(interactive_touch_->max_pressure) + "\nc\nw 2\n";
            input_failure = write_touch_protocol(protocol, cancellation);
            if (!input_failure && !wait_touch_delay(std::chrono::milliseconds{2}, cancellation))
                input_failure = fail(ConnectionErrorCode::Canceled, "swipe", "Swipe was canceled.");
        }
        if (!input_failure && extra_swipe)
        {
            if (!wait_touch_delay(std::chrono::milliseconds{150}, cancellation))
                input_failure = fail(ConnectionErrorCode::Canceled, "swipe", "Swipe was canceled.");
            std::optional<SessionFailure> post_swipe_geometry_failure;
            auto const post_swipe_geometry = current_geometry(cancellation,
                post_swipe_geometry_failure);
            if (post_swipe_geometry_failure || !post_swipe_geometry
                || post_swipe_geometry->width != source_frame.width
                || post_swipe_geometry->height != source_frame.height
                || post_swipe_geometry->orientation != source_frame.orientation
                || source_frame.geometry_generation != geometry_generation_)
                input_failure = post_swipe_geometry_failure.value_or(fail(
                    ConnectionErrorCode::DeviceNotReady, "swipe",
                    "Device screen geometry changed before extra movement."));
        }
        if (!input_failure && extra_swipe)
        {
            auto const extra = scale_touch_point(second->first, second->second - 100, source_frame.width,
                source_frame.height, source_frame.orientation);
            protocol = "m 0 " + std::to_string(extra.first) + " "
                + std::to_string(extra.second) + " "
                + std::to_string(interactive_touch_->max_pressure) + "\nc\nw 500\n";
            input_failure = write_touch_protocol(protocol, cancellation);
            if (!input_failure && !wait_touch_delay(std::chrono::milliseconds{500}, cancellation))
                input_failure = fail(ConnectionErrorCode::Canceled, "swipe", "Swipe was canceled.");
        }
        if (!input_failure)
            input_failure = write_touch_protocol("u 0\nc\nw 50\n", cancellation);
        if (!input_failure && !wait_touch_delay(std::chrono::milliseconds{50}, cancellation))
            input_failure = fail(ConnectionErrorCode::Canceled, "swipe", "Swipe was canceled.");
        if (input_failure)
        {
            auto const cleanup_failure = stop_interactive_touch(true);
            return {.failure = cleanup_failure ? cleanup_failure : input_failure,
                    .source_frame = source_frame, .requested_first_point = first_request,
                    .requested_second_point = second_request};
        }
        interactive_touch_->pressed = false;
    }
    else
    {
    auto run_swipe = [&](std::pair<std::int32_t, std::int32_t> const& from,
                         std::pair<std::int32_t, std::int32_t> const& to,
                         std::int32_t duration) {
        std::vector<std::string> arguments{"-s", device_.serial, "shell", "input"};
        if (options_.display_id)
        {
            arguments.insert(arguments.end(), {"-d", std::to_string(*options_.display_id)});
        }
        arguments.insert(arguments.end(), {"swipe", std::to_string(from.first),
            std::to_string(from.second), std::to_string(to.first),
            std::to_string(to.second), std::to_string(duration)});
        std::string command_output;
        auto const failure = run_command(std::move(arguments), std::chrono::seconds(15),
            cancellation, kMaxTextBytes, command_output);
        output = std::move(command_output);
        return failure;
    };
    auto const command_failure = run_swipe(*first, *second, scaled_duration);
    if (command_failure)
        return {.failure = command_failure, .source_frame = source_frame,
                .requested_first_point = first_request,
                .requested_second_point = second_request};
    if (extra_swipe && options_.extra_swipe_duration_ms > 0)
    {
        if (cancellation.stop_requested())
        {
            return {.failure = fail(ConnectionErrorCode::Canceled,
                                    "swipe", "Swipe was canceled before its extra movement."),
                    .source_frame = source_frame, .requested_first_point = first_request,
                    .requested_second_point = second_request};
        }
        std::optional<SessionFailure> post_swipe_geometry_failure;
        auto const post_swipe_geometry = current_geometry(cancellation,
            post_swipe_geometry_failure);
        if (post_swipe_geometry_failure || !post_swipe_geometry
            || post_swipe_geometry->width != source_frame.width
            || post_swipe_geometry->height != source_frame.height
            || post_swipe_geometry->orientation != source_frame.orientation
            || source_frame.geometry_generation != geometry_generation_)
        {
            return {.failure = post_swipe_geometry_failure.value_or(
                        fail(ConnectionErrorCode::DeviceNotReady, "swipe",
                             "Device screen geometry changed before extra movement.")),
                    .source_frame = source_frame, .requested_first_point = first_request,
                    .requested_second_point = second_request};
        }
        std::pair<std::int32_t, std::int32_t> const extra_end{
            second->first, second->second - options_.extra_swipe_distance};
        if (auto const extra_failure = run_swipe(*second, extra_end,
                                                options_.extra_swipe_duration_ms))
        {
            return {.failure = extra_failure, .source_frame = source_frame,
                    .requested_first_point = first_request,
                    .requested_second_point = second_request};
        }
    }
    }
    return {
        .source_frame = source_frame,
        .requested_first_point = first_request,
        .requested_second_point = second_request,
        .first_point = DeviceOperationResult::Point{
            .canonical_x = x1, .canonical_y = y1,
            .device_x = first->first, .device_y = first->second,
        },
        .second_point = DeviceOperationResult::Point{
            .canonical_x = x2, .canonical_y = y2,
            .device_x = second->first, .device_y = second->second,
        },
    };
}

DeviceOperationResult DeviceSession::prepare_touch(
    std::uint64_t frame_id,
    std::stop_token cancellation)
{
    std::lock_guard lock(mutex_);
    if (frame_id == 0)
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "touch_prepare", "A source frame is required for touch preparation.")};
    if (held_touch_ || (interactive_touch_ && interactive_touch_->pressed))
        return {.failure = fail(ConnectionErrorCode::Busy,
                                "touch_prepare", "Cannot prepare a new touch while a press is active.")};

    // A failed or canceled re-prepare must never leave an older geometry or
    // backend configuration eligible for a later DOWN.
    prepared_touch_.reset();
    prepared_touch_device_.reset();

    auto const frame_it = frames_.find(frame_id);
    if (frame_it == frames_.end()
        || frame_it->second.metadata.session_generation != session_generation_
        || frame_it->second.metadata.geometry_generation != geometry_generation_)
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "touch_prepare", "Screenshot frame is no longer current.")};

    auto const source_frame = frame_it->second.metadata;
    std::optional<SessionFailure> geometry_failure;
    auto const geometry = current_geometry(cancellation, geometry_failure);
    if (geometry_failure)
        return {.failure = std::move(geometry_failure), .source_frame = source_frame};
    if (!geometry || geometry->width != source_frame.width
        || geometry->height != source_frame.height
        || geometry->orientation != source_frame.orientation
        || source_frame.geometry_generation != geometry_generation_)
        return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                "touch_prepare", "Device screen geometry changed after capture."),
                .source_frame = source_frame};

    std::optional<HeldTouchDevice> prepared_device;
    if (options_.touch_backend == DeviceSessionOptions::TouchBackend::adb)
    {
        prepared_device = find_touch_device(cancellation, geometry_failure);
        if (geometry_failure)
            return {.failure = std::move(geometry_failure), .source_frame = source_frame};
        if (!prepared_device)
            return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                    "touch_discovery", "No writable touch device was found."),
                    .source_frame = source_frame};
    }
    else if (auto const start_failure = ensure_interactive_touch(
                 source_frame.width, source_frame.height, source_frame.orientation, cancellation))
    {
        return {.failure = std::move(start_failure), .source_frame = source_frame};
    }

    prepared_touch_ = PreparedTouch{
        .session_generation = session_generation_,
        .geometry_generation = geometry_generation_,
        .backend = options_.touch_backend,
        .screen_width = source_frame.width,
        .screen_height = source_frame.height,
        .orientation = source_frame.orientation,
    };
    prepared_touch_device_ = std::move(prepared_device);
    return {.source_frame = source_frame};
}

DeviceOperationResult DeviceSession::touch(
    std::uint64_t frame_id,
    std::int32_t canonical_x,
    std::int32_t canonical_y,
    HeldTouchPhase phase,
    std::stop_token cancellation)
{
    std::lock_guard lock(mutex_);
    if (phase != HeldTouchPhase::down)
    {
        auto const active_frame = interactive_touch_ && interactive_touch_->pressed
            ? interactive_touch_frame_ : held_touch_frame_;
        if ((!held_touch_ && !(interactive_touch_ && interactive_touch_->pressed))
            || !active_frame)
        {
            return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                    "touch_release", "No held touch is active.")};
        }
        auto const source = *active_frame;
        if (frame_id == 0 || frame_id != source.id)
        {
            return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                    "touch_release", "Touch release does not match the active press.")};
        }
        if (interactive_touch_ && interactive_touch_->pressed)
        {
            auto const protocol = phase == HeldTouchPhase::up
                ? std::string{"u 0\nc\n"} : std::string{"r\n"};
            auto const input_failure = write_touch_protocol(protocol, std::stop_token{});
            if (input_failure)
            {
                auto const stop_failure = stop_interactive_touch(false);
                return {.failure = stop_failure ? stop_failure : input_failure,
                        .source_frame = source};
            }
            interactive_touch_->pressed = false;
            interactive_touch_frame_.reset();
            return {.source_frame = source};
        }
        auto const release_failure = release_held_touch(std::stop_token{});
        if (release_failure)
            return {.failure = release_failure, .source_frame = source};
        return {.source_frame = source};
    }

    if (held_touch_ || (interactive_touch_ && interactive_touch_->pressed))
    {
        return {.failure = fail(ConnectionErrorCode::Busy,
                                "touch_down", "A held touch is already active.")};
    }
    auto frame_it = frames_.find(frame_id);
    if (frame_it == frames_.end()
        || frame_it->second.metadata.session_generation != session_generation_
        || frame_it->second.metadata.geometry_generation != geometry_generation_)
    {
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "touch_down", "Screenshot frame is no longer current.")};
    }
    auto const source_frame = frame_it->second.metadata;
    if (frame_id <= last_touch_frame_id_)
    {
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "touch_down", "A frame can start only one held-touch press.")};
    }
    DeviceOperationResult::Point const request{
        .canonical_x = canonical_x, .canonical_y = canonical_y,
    };
    std::optional<SessionFailure> geometry_failure;
    auto const geometry = current_geometry(cancellation, geometry_failure);
    if (geometry_failure)
        return {.failure = std::move(geometry_failure), .source_frame = source_frame,
                .requested_first_point = request};
    if (!geometry || geometry->width != source_frame.width
        || geometry->height != source_frame.height
        || geometry->orientation != source_frame.orientation
        || source_frame.geometry_generation != geometry_generation_)
    {
        return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                "touch_down", "Device screen geometry changed after capture."),
                .source_frame = source_frame, .requested_first_point = request};
    }
    auto const prepared_matches = prepared_touch_
        && prepared_touch_->session_generation == session_generation_
        && prepared_touch_->geometry_generation == geometry_generation_
        && prepared_touch_->backend == options_.touch_backend
        && prepared_touch_->screen_width == source_frame.width
        && prepared_touch_->screen_height == source_frame.height
        && prepared_touch_->orientation == source_frame.orientation;
    if (!prepared_matches)
    {
        return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                "touch_prepare", "Held touch was not prepared for the current session and geometry."),
                .source_frame = source_frame, .requested_first_point = request};
    }
    auto const point = transform_point(canonical_x, canonical_y,
        source_frame.width, source_frame.height, source_frame.orientation,
        DisplayTransform{source_frame.display_scale, source_frame.display_offset_x,
                         source_frame.display_offset_y});
    if (!point)
    {
        return {.failure = fail(ConnectionErrorCode::InvalidArgument,
                                "touch_down", "Touch coordinates are outside the canonical screen."),
                .source_frame = source_frame, .requested_first_point = request};
    }

    if (options_.touch_backend != DeviceSessionOptions::TouchBackend::adb)
    {
        if (!interactive_touch_ || !interactive_touch_->process
            || interactive_touch_->process->has_exited()
            || interactive_touch_->screen_width != source_frame.width
            || interactive_touch_->screen_height != source_frame.height
            || interactive_touch_->orientation != source_frame.orientation)
        {
            prepared_touch_.reset();
            prepared_touch_device_.reset();
            return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                    "touch_prepare", "Prepared touch helper is no longer ready; prepare again before DOWN."),
                    .source_frame = source_frame, .requested_first_point = request};
        }
        auto const touch_point = scale_touch_point(point->first, point->second,
            source_frame.width, source_frame.height, source_frame.orientation);
        interactive_touch_frame_ = source_frame;
        interactive_touch_->pressed = true;
        auto const protocol = "d 0 " + std::to_string(touch_point.first) + " "
            + std::to_string(touch_point.second) + " "
            + std::to_string(interactive_touch_->max_pressure) + "\nc\n";
        if (auto const input_failure = write_touch_protocol(protocol, cancellation))
        {
            auto const cleanup_failure = stop_interactive_touch(true);
            return {.failure = cleanup_failure ? cleanup_failure : input_failure,
                    .source_frame = source_frame, .requested_first_point = request};
        }
        last_touch_frame_id_ = frame_id;
        return {.source_frame = source_frame,
            .requested_first_point = request,
            .first_point = DeviceOperationResult::Point{
                .canonical_x = canonical_x, .canonical_y = canonical_y,
                .device_x = touch_point.first, .device_y = touch_point.second,
            }};
    }

    auto const touch_device = prepared_touch_device_;
    if (!touch_device)
    {
        prepared_touch_.reset();
        return {.failure = fail(ConnectionErrorCode::DeviceNotReady,
                                "touch_prepare", "Prepared touch device is no longer available; prepare again before DOWN."),
                .source_frame = source_frame, .requested_first_point = request};
    }
    auto const [touch_x, touch_y] = scale_raw_touch_point(
        point->first, point->second, source_frame.width, source_frame.height,
        touch_device->max_x, touch_device->max_y, source_frame.orientation);
    held_touch_ = *touch_device;
    held_touch_frame_ = source_frame;
    last_touch_frame_id_ = frame_id;
    std::string command = "set -e; sendevent " + touch_device->path + " 3 47 0; sendevent "
        + touch_device->path + " 3 57 123; sendevent " + touch_device->path
        + " 3 53 " + std::to_string(touch_x) + "; sendevent " + touch_device->path
        + " 3 54 " + std::to_string(touch_y) + ";";
    if (touch_device->has_tool_finger)
        command += " sendevent " + touch_device->path + " 1 325 1;";
    if (touch_device->has_touch)
        command += " sendevent " + touch_device->path + " 1 330 1;";
    command += " sendevent " + touch_device->path + " 0 0 0";
    std::string output;
    auto const command_failure = run_command(
        {"-s", device_.serial, "shell", "sh", "-c",
         quote_android_shell_argument(command)},
        std::chrono::seconds(15), cancellation, kMaxTextBytes, output);
    if (command_failure)
    {
        // The DOWN command may have reached Android before ADB reported a
        // cancellation or pipe failure. Attempt release even on that path.
        auto const release_failure = release_held_touch(std::stop_token{});
        return {.failure = release_failure ? release_failure : command_failure,
                .source_frame = source_frame, .requested_first_point = request};
    }
    return {
        .source_frame = source_frame,
        .requested_first_point = request,
        .first_point = DeviceOperationResult::Point{
            .canonical_x = canonical_x, .canonical_y = canonical_y,
            .device_x = point->first, .device_y = point->second,
        },
    };
}

bool DeviceSession::connected(std::stop_token cancellation)
{
    std::lock_guard lock(mutex_);
    std::vector<std::string> arguments{"-s", device_.serial, "get-state"};
    if (auto invocation = profile_->expand(profile_name_, "get_state", adb_path_, device_.serial))
        arguments = invocation->arguments;
    std::string output;
    auto const command_failure = run_command(
        std::move(arguments), std::chrono::seconds(5), cancellation, kMaxTextBytes, output);
    return !command_failure && trim(std::move(output)) == "device";
}

bool DeviceSession::listed_as_ready(std::stop_token cancellation)
{
    std::lock_guard lock(mutex_);
    std::string output;
    auto const command_failure = run_command(
        {"devices"}, std::chrono::seconds(15), cancellation, kMaxTextBytes, output, true);
    if (command_failure) return false;

    std::istringstream lines(output);
    std::string line;
    while (std::getline(lines, line))
    {
        std::istringstream fields(line);
        std::string serial;
        std::string state;
        fields >> serial >> state;
        if (serial == device_.serial && state == "device") return true;
    }
    return false;
}

std::uint64_t DeviceSession::session_generation() const noexcept
{
    return session_generation_;
}

std::optional<DeviceFrameMetadata> DeviceSession::frame_metadata(std::uint64_t frame_id) const
{
    std::lock_guard lock(mutex_);
    auto const found = frames_.find(frame_id);
    if (found == frames_.end()) return std::nullopt;
    return found->second.metadata;
}

std::shared_ptr<const Vision::GrayImage> DeviceSession::frame_pixels(std::uint64_t frame_id) const
{
    std::lock_guard lock(mutex_);
    auto const found = frames_.find(frame_id);
    return found == frames_.end() ? nullptr : found->second.pixels;
}

bool DeviceSession::copy_frame_png(
    std::uint64_t frame_id,
    std::uint8_t* destination,
    std::uint64_t capacity) const noexcept
{
    std::lock_guard lock(mutex_);
    auto const found = frames_.find(frame_id);
    if (found == frames_.end() || destination == nullptr
        || capacity < found->second.png.size())
        return false;
    std::copy(found->second.png.begin(), found->second.png.end(), destination);
    return true;
}

bool DeviceSession::release_frame(std::uint64_t frame_id) noexcept
{
    std::lock_guard lock(mutex_);
    return frames_.erase(frame_id) != 0;
}

std::optional<SessionFailure> DeviceSession::run_command(
    std::vector<std::string> arguments,
    std::chrono::milliseconds timeout,
    std::stop_token cancellation,
    std::size_t max_standard_output_bytes,
    std::string& standard_output,
    bool leave_children_running_on_success,
    AdbCommandResult* command_result)
{
    if (cancellation.stop_requested())
        return fail(ConnectionErrorCode::Canceled, "command", "Operation was canceled.");
    if (!std::filesystem::exists(adb_path_))
        return fail(ConnectionErrorCode::AdbExecutableNotFound, "command", "ADB executable was not found.");
    auto process = create_win32_process();
    AdbCommandRunnerWin32 runner{*process};
    auto result = runner.run(
        AdbInvocation{adb_path_, std::move(arguments), leave_children_running_on_success},
        timeout, cancellation,
        max_standard_output_bytes);
    if (command_result != nullptr) *command_result = result;
    if (result.canceled)
        return fail(ConnectionErrorCode::Canceled, "command", "Operation was canceled.");
    if (result.timed_out)
        return fail(ConnectionErrorCode::CommandTimedOut, "command", "ADB command timed out.");
    if (!result.started)
        return fail(ConnectionErrorCode::ProcessStartFailed, "command", "ADB process could not be started.");
    if (result.standard_output_truncated)
        return fail(ConnectionErrorCode::InvalidDeviceResponse, "command", "ADB output exceeded its size limit.");
    if (result.exit_code != 0)
    {
        std::string message = trim(std::move(result.standard_error));
        if (message.empty()) message = "ADB command failed.";
        return fail(ConnectionErrorCode::CommandFailed, "command", std::move(message));
    }
    standard_output = std::move(result.standard_output);
    return std::nullopt;
}

std::optional<std::pair<std::int32_t, std::int32_t>> DeviceSession::current_size(
    std::stop_token cancellation,
    std::optional<SessionFailure>& failure)
{
    std::vector<std::string> arguments{"-s", device_.serial, "shell", "wm", "size"};
    if (auto invocation = profile_->expand(profile_name_, "get_size", adb_path_, device_.serial))
        arguments = invocation->arguments;
    if (options_.display_id)
    {
        arguments.push_back("-d");
        arguments.push_back(std::to_string(*options_.display_id));
    }
    std::string output;
    failure = run_command(
        std::move(arguments), std::chrono::seconds(5), cancellation, kMaxTextBytes, output);
    if (failure) return std::nullopt;
    auto dimensions = parse_size(output);
    if (!dimensions)
        failure = fail(ConnectionErrorCode::InvalidDeviceResponse,
                       "get_size", "ADB returned an invalid screen size.");
    return dimensions;
}

std::optional<DeviceSession::Geometry> DeviceSession::current_geometry(
    std::stop_token cancellation,
    std::optional<SessionFailure>& failure)
{
    std::optional<std::pair<std::int32_t, std::int32_t>> dimensions;
    std::optional<std::int32_t> orientation;
    std::string output;
    auto const size_invocation = profile_->expand(profile_name_, "get_size", adb_path_, device_.serial);
    const std::vector<std::string> standard_size{"-s", device_.serial, "shell", "wm", "size"};
    // Batch only the standard primary-display query. Authored commands and
    // selected-display routing keep their original arguments and semantics.
    if (geometry_batch_supported_ && !options_.display_id
        && (!size_invocation || size_invocation->arguments == standard_size))
    {
        constexpr std::string_view separator{"__UMA_GEOMETRY_ORIENTATION__"};
        auto const query = quote_android_shell_argument(
            "wm size && printf '\\n__UMA_GEOMETRY_ORIENTATION__\\n' && dumpsys input");
        auto const batch_failure = run_command(
            {"-s", device_.serial, "shell", "sh", "-c", query},
            std::chrono::seconds(5), cancellation, 1024 * 1024, output);
        if (cancellation.stop_requested())
        {
            failure = batch_failure.value_or(fail(ConnectionErrorCode::Canceled,
                "command", "Operation was canceled."));
            return std::nullopt;
        }
        auto const split = output.find(separator);
        if (!batch_failure && split != std::string::npos)
        {
            dimensions = parse_size(output.substr(0, split));
            orientation = parse_orientation(output.substr(split + separator.size()));
        }
        if (!dimensions || !orientation)
        {
            // An older shell or unexpected output must not break input. Use
            // the original checked queries for the rest of this session.
            geometry_batch_supported_ = false;
            dimensions.reset();
            orientation.reset();
        }
    }
    if (!dimensions || !orientation)
    {
        dimensions = current_size(cancellation, failure);
        if (failure || !dimensions) return std::nullopt;
        failure = run_command(
            options_.display_id
                ? std::vector<std::string>{"-s", device_.serial, "shell", "dumpsys", "display"}
                : std::vector<std::string>{"-s", device_.serial, "shell", "dumpsys", "input"},
            std::chrono::seconds(5), cancellation, 1024 * 1024, output);
        if (failure) return std::nullopt;
        orientation = options_.display_id
            ? parse_display_orientation(output, *options_.display_id)
            : parse_orientation(output);
        if (!orientation)
        {
            failure = fail(ConnectionErrorCode::InvalidDeviceResponse,
                           "get_orientation", "ADB returned an unknown orientation for the selected display.");
            return std::nullopt;
        }
    }

    auto width = dimensions->first;
    auto height = dimensions->second;
    // `wm size` reports the panel's natural dimensions. Rotation is relative
    // to that natural orientation, which can be portrait or landscape; every
    // quarter turn swaps the current logical width and height.
    if ((*orientation % 2) != 0)
        std::swap(width, height);

    Geometry geometry{
        .width = width,
        .height = height,
        .orientation = *orientation,
    };
    if (!last_geometry_ || *last_geometry_ != geometry)
    {
        if (last_geometry_ && (held_touch_
            || (interactive_touch_ && interactive_touch_->pressed)))
        {
            auto const release_failure = release_held_touch(std::stop_token{});
            if (release_failure)
            {
                failure = release_failure;
                return std::nullopt;
            }
        }
        last_geometry_ = geometry;
        ++geometry_generation_;
        if (geometry_generation_ == 0) ++geometry_generation_;
    }
    return geometry;
}

std::optional<std::pair<std::int32_t, std::int32_t>> DeviceSession::decode_png(
    std::vector<std::uint8_t> const& bytes, Vision::GrayImage* pixels)
{
    if (!valid_png_chunks(bytes)) return std::nullopt;

    ComScope com;
    if (FAILED(com.result) && com.result != RPC_E_CHANGED_MODE) return std::nullopt;
    HGLOBAL memory = ::GlobalAlloc(GMEM_MOVEABLE, bytes.size());
    if (memory == nullptr) return std::nullopt;
    void* const destination = ::GlobalLock(memory);
    if (destination == nullptr)
    {
        ::GlobalFree(memory);
        return std::nullopt;
    }
    std::copy(bytes.begin(), bytes.end(), static_cast<std::uint8_t*>(destination));
    ::GlobalUnlock(memory);

    Microsoft::WRL::ComPtr<IStream> stream;
    HRESULT result = ::CreateStreamOnHGlobal(memory, TRUE, &stream);
    if (FAILED(result))
    {
        ::GlobalFree(memory);
        return std::nullopt;
    }
    Microsoft::WRL::ComPtr<IWICImagingFactory> factory;
    result = ::CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_PPV_ARGS(&factory));
    if (FAILED(result)) return std::nullopt;
    Microsoft::WRL::ComPtr<IWICBitmapDecoder> decoder;
    result = factory->CreateDecoderFromStream(stream.Get(), nullptr,
                                              WICDecodeMetadataCacheOnLoad, &decoder);
    if (FAILED(result)) return std::nullopt;
    Microsoft::WRL::ComPtr<IWICBitmapFrameDecode> frame;
    result = decoder->GetFrame(0, &frame);
    if (FAILED(result)) return std::nullopt;
    UINT width = 0;
    UINT height = 0;
    result = frame->GetSize(&width, &height);
    if (FAILED(result) || width == 0 || height == 0 || width > 16384 || height > 16384)
        return std::nullopt;
    constexpr std::uint64_t kMaxDecodedPixels = 16ULL * 1024 * 1024;
    auto const pixel_count = static_cast<std::uint64_t>(width) * height;
    if (pixel_count > kMaxDecodedPixels) return std::nullopt;
    if (pixels && (width > 10000 || height > 10000)) return std::nullopt;
    Microsoft::WRL::ComPtr<IWICFormatConverter> converter;
    result = factory->CreateFormatConverter(&converter);
    if (FAILED(result)) return std::nullopt;
    result = converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA,
                                   WICBitmapDitherTypeNone, nullptr,
                                   0.0, WICBitmapPaletteTypeCustom);
    if (FAILED(result)) return std::nullopt;
    auto const stride = static_cast<UINT64>(width) * 4;
    auto const decoded_bytes = stride * height;
    if (stride > (std::numeric_limits<UINT>::max)()
        || decoded_bytes > kMaxDecodedPixels * 4)
        return std::nullopt;
    std::vector<std::uint8_t> decoded(static_cast<std::size_t>(decoded_bytes));
    result = converter->CopyPixels(
        nullptr, static_cast<UINT>(stride),
        static_cast<UINT>(decoded.size()), decoded.data());
    if (FAILED(result)) return std::nullopt;
    if (pixels)
    {
        pixels->width = static_cast<std::int32_t>(width);
        pixels->height = static_cast<std::int32_t>(height);
        pixels->rgba = std::move(decoded);
        pixels->gray.resize(static_cast<std::size_t>(pixel_count));
        // WIC's straight BGRA becomes the same straight RGBA and integer
        // grayscale planes that the task context's decoder previously built.
        for (std::size_t index = 0; index < pixels->gray.size(); ++index)
        {
            auto const offset = index * 4;
            std::swap(pixels->rgba[offset], pixels->rgba[offset + 2]);
            pixels->gray[index] = static_cast<std::uint8_t>(
                (static_cast<std::uint32_t>(pixels->rgba[offset]) * 299u
                    + static_cast<std::uint32_t>(pixels->rgba[offset + 1]) * 587u
                    + static_cast<std::uint32_t>(pixels->rgba[offset + 2]) * 114u) / 1000u);
        }
    }
    return std::pair{static_cast<std::int32_t>(width), static_cast<std::int32_t>(height)};
}

}
