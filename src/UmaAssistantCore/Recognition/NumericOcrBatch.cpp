#include "NumericOcrBatch.hpp"

#include "../Connection/AdbCommandRunner.hpp"
#include "NumericImageProcessing.hpp"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <Windows.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <fstream>
#include <limits>
#include <set>
#include <stdexcept>
#include <string_view>
#include <system_error>
#include <utility>

namespace UmaAssistant::Recognition {
namespace {

using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;

constexpr auto frame_budget = std::chrono::seconds(5);
constexpr std::size_t max_tsv_bytes = 4u * 1024u * 1024u;

class ComApartment
{
public:
    ComApartment()
    {
        auto const status = ::CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(status) && status != RPC_E_CHANGED_MODE)
            throw std::runtime_error("Could not initialize COM for numeric OCR image encoding.");
        owns_initialization_ = SUCCEEDED(status);
    }

    ~ComApartment()
    {
        if (owns_initialization_) ::CoUninitialize();
    }

    ComApartment(ComApartment const&) = delete;
    ComApartment& operator=(ComApartment const&) = delete;

private:
    bool owns_initialization_{};
};

class TemporaryBatchDirectory
{
public:
    TemporaryBatchDirectory()
    {
        static std::atomic<std::uint64_t> next_id{};
        auto const base = std::filesystem::temp_directory_path();
        for (int attempt = 0; attempt < 32; ++attempt)
        {
            auto const id = next_id.fetch_add(1, std::memory_order_relaxed);
            path_ = base / ("uma-numeric-ocr-" + std::to_string(::GetCurrentProcessId())
                + "-" + std::to_string(id));
            std::error_code error;
            if (std::filesystem::create_directory(path_, error)) return;
            if (error && error != std::errc::file_exists)
                throw std::filesystem::filesystem_error("Could not create numeric OCR temp directory.", path_, error);
        }
        throw std::runtime_error("Could not allocate a unique numeric OCR temp directory.");
    }

    ~TemporaryBatchDirectory()
    {
        std::error_code ignored;
        std::filesystem::remove_all(path_, ignored);
    }

    [[nodiscard]] std::filesystem::path const& path() const noexcept { return path_; }

    TemporaryBatchDirectory(TemporaryBatchDirectory const&) = delete;
    TemporaryBatchDirectory& operator=(TemporaryBatchDirectory const&) = delete;

private:
    std::filesystem::path path_;
};

[[nodiscard]] Vision::GrayImage resize_nearest(Vision::GrayImage const& source, bool padded)
{
    if (source.width <= 0 || source.height <= 0
        || source.width > 10000 || source.height > 10000
        || source.gray.size() != static_cast<std::size_t>(source.width) * source.height)
        throw std::invalid_argument("Numeric OCR requires a valid grayscale crop.");

    int const border = padded ? (std::max)(6, source.height / 10) : 0;
    std::int64_t const scaled_width = static_cast<std::int64_t>(source.width) * 2 + border * 4ll;
    std::int64_t const scaled_height = static_cast<std::int64_t>(source.height) * 2 + border * 4ll;
    if (scaled_width <= 0 || scaled_height <= 0 || scaled_width > 10000 || scaled_height > 10000)
        throw std::invalid_argument("Numeric OCR image dimensions exceed 10000 pixels.");

    int const width = static_cast<int>(scaled_width);
    int const height = static_cast<int>(scaled_height);
    int const offset_x = border * 2;
    int const offset_y = border * 2;
    Vision::GrayImage resized;
    resized.width = width;
    resized.height = height;
    resized.gray.assign(static_cast<std::size_t>(width) * height, 255);
    for (int y = 0; y < source.height; ++y)
    {
        auto const* input = source.gray.data() + static_cast<std::size_t>(y) * source.width;
        auto* first = resized.gray.data() + static_cast<std::size_t>(offset_y + y * 2) * width + offset_x;
        auto* second = first + width;
        for (int x = 0; x < source.width; ++x)
        {
            auto const value = input[x];
            first[x * 2] = value;
            first[x * 2 + 1] = value;
            second[x * 2] = value;
            second[x * 2 + 1] = value;
        }
    }
    return resized;
}

void save_png(std::filesystem::path const& path, Vision::GrayImage const& image)
{
    auto const pixel_count = static_cast<std::size_t>(image.width) * image.height;
    std::vector<std::uint8_t> bgra(pixel_count * 4u);
    for (std::size_t index = 0; index < pixel_count; ++index)
    {
        auto const gray = image.gray[index];
        auto const offset = index * 4u;
        bgra[offset] = gray;
        bgra[offset + 1] = gray;
        bgra[offset + 2] = gray;
        bgra[offset + 3] = 255;
    }

    ComPtr<IWICImagingFactory> factory;
    HRESULT status = ::CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&factory));
    if (FAILED(status)) throw std::runtime_error("Could not create the Windows Imaging Component factory.");

    ComPtr<IWICStream> stream;
    status = factory->CreateStream(&stream);
    if (FAILED(status)) throw std::runtime_error("Could not create the numeric OCR PNG stream.");
    status = stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE);
    if (FAILED(status)) throw std::runtime_error("Could not open a numeric OCR PNG temp file.");

    ComPtr<IWICBitmapEncoder> encoder;
    status = factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder);
    if (FAILED(status)) throw std::runtime_error("Could not create the numeric OCR PNG encoder.");
    status = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
    if (FAILED(status)) throw std::runtime_error("Could not initialize the numeric OCR PNG encoder.");

    ComPtr<IWICBitmapFrameEncode> frame;
    ComPtr<IPropertyBag2> properties;
    status = encoder->CreateNewFrame(&frame, &properties);
    if (FAILED(status)) throw std::runtime_error("Could not create a numeric OCR PNG frame.");
    status = frame->Initialize(properties.Get());
    if (FAILED(status)) throw std::runtime_error("Could not initialize a numeric OCR PNG frame.");
    status = frame->SetSize(static_cast<UINT>(image.width), static_cast<UINT>(image.height));
    if (FAILED(status)) throw std::runtime_error("Could not set numeric OCR PNG dimensions.");

    WICPixelFormatGUID pixel_format = GUID_WICPixelFormat32bppBGRA;
    status = frame->SetPixelFormat(&pixel_format);
    if (FAILED(status) || pixel_format != GUID_WICPixelFormat32bppBGRA)
        throw std::runtime_error("The PNG encoder does not support the requested numeric OCR pixel format.");
    auto const stride = static_cast<UINT>(image.width) * 4u;
    status = frame->WritePixels(static_cast<UINT>(image.height), stride,
        static_cast<UINT>(bgra.size()), bgra.data());
    if (FAILED(status)) throw std::runtime_error("Could not write numeric OCR PNG pixels.");
    status = frame->Commit();
    if (FAILED(status)) throw std::runtime_error("Could not commit the numeric OCR PNG frame.");
    status = encoder->Commit();
    if (FAILED(status)) throw std::runtime_error("Could not commit the numeric OCR PNG file.");
}

[[nodiscard]] std::string utf8_path(std::filesystem::path const& path)
{
    auto const encoded = path.u8string();
    return {encoded.begin(), encoded.end()};
}

void write_image_list(std::filesystem::path const& list_path,
    std::span<std::filesystem::path const> images)
{
    std::ofstream output(list_path, std::ios::binary | std::ios::trunc);
    if (!output) throw std::runtime_error("Could not write the numeric OCR input list.");
    for (auto const& image : images)
    {
        auto const value = utf8_path(image);
        output.write(value.data(), static_cast<std::streamsize>(value.size()));
        output.put('\n');
    }
    if (!output) throw std::runtime_error("Could not finish writing the numeric OCR input list.");
}

[[nodiscard]] bool ascii_space(char value) noexcept
{
    return value == ' ' || value == '\t' || value == '\r' || value == '\n' || value == '\f' || value == '\v';
}

[[nodiscard]] bool ascii_digit(char value) noexcept { return value >= '0' && value <= '9'; }

[[nodiscard]] std::size_t unicode_space_bytes(std::string_view text, std::size_t index) noexcept
{
    auto const remaining = text.size() - index;
    auto const first = static_cast<unsigned char>(text[index]);
    if (remaining >= 2 && first == 0xC2)
    {
        auto const second = static_cast<unsigned char>(text[index + 1]);
        if (second == 0x85 || second == 0xA0) return 2; // NEL, no-break space
    }
    if (remaining >= 3)
    {
        auto const second = static_cast<unsigned char>(text[index + 1]);
        auto const third = static_cast<unsigned char>(text[index + 2]);
        if (first == 0xE1 && second == 0x9A && third == 0x80) return 3; // Ogham space mark
        if (first == 0xE2 && second == 0x80
            && ((third >= 0x80 && third <= 0x8A) || third == 0xA8 || third == 0xA9 || third == 0xAF))
            return 3; // U+2000..U+200A, line/paragraph separator, narrow no-break space
        if (first == 0xE2 && second == 0x81 && third == 0x9F) return 3; // medium mathematical space
        if (first == 0xE3 && second == 0x80 && third == 0x80) return 3; // ideographic space
    }
    return 0;
}

[[nodiscard]] bool is_blank_utf8(std::string_view text) noexcept
{
    for (std::size_t index = 0; index < text.size();)
    {
        if (ascii_space(text[index]))
        {
            ++index;
            continue;
        }
        auto const width = unicode_space_bytes(text, index);
        if (width == 0) return false;
        index += width;
    }
    return true;
}

[[nodiscard]] std::string normalize_unicode_whitespace(std::string_view text)
{
    std::string normalized;
    normalized.reserve(text.size());
    for (std::size_t index = 0; index < text.size();)
    {
        if (ascii_space(text[index]))
        {
            normalized.push_back(' ');
            ++index;
            continue;
        }
        auto const width = unicode_space_bytes(text, index);
        if (width != 0)
        {
            normalized.push_back(' ');
            index += width;
            continue;
        }
        normalized.push_back(text[index++]);
    }
    return normalized;
}

[[nodiscard]] std::string remove_failure_label(std::string const& raw)
{
    std::string result;
    result.reserve(raw.size());
    for (std::size_t index = 0; index < raw.size();)
    {
        constexpr std::string_view label = "failure";
        bool matches = index + label.size() <= raw.size();
        for (std::size_t offset = 0; matches && offset < label.size(); ++offset)
        {
            auto const value = raw[index + offset];
            auto const lower = value >= 'A' && value <= 'Z' ? static_cast<char>(value + ('a' - 'A')) : value;
            matches = lower == label[offset];
        }
        auto const word_char = [](char c)
        {
            auto const byte = static_cast<unsigned char>(c);
            return (byte >= 'a' && byte <= 'z') || (byte >= 'A' && byte <= 'Z')
                || (byte >= '0' && byte <= '9') || byte >= 0x80 || c == '_';
        };
        bool const left_boundary = index == 0 || !word_char(raw[index - 1]);
        bool const right_boundary = matches
            && (index + label.size() == raw.size() || !word_char(raw[index + label.size()]));
        if (matches && left_boundary && right_boundary)
        {
            index += label.size();
            continue;
        }
        result.push_back(raw[index++]);
    }
    return result;
}

[[nodiscard]] bool contains_negative_number(std::string_view text) noexcept
{
    constexpr std::string_view unicode_minus{"\xE2\x88\x92", 3};
    for (std::size_t index = 0; index < text.size(); ++index)
    {
        std::size_t after = index + 1;
        if (text[index] == '-')
        {
            // continue below
        }
        else if (text.substr(index, unicode_minus.size()) == unicode_minus)
        {
            after = index + unicode_minus.size();
        }
        else
        {
            continue;
        }
        while (after < text.size() && ascii_space(text[after])) ++after;
        if (after < text.size() && ascii_digit(text[after])) return true;
    }
    return false;
}

[[nodiscard]] std::optional<int> parse_numeric(std::string const& raw, int maximum)
{
    auto normalized = normalize_unicode_whitespace(raw);
    if (contains_negative_number(normalized)) return std::nullopt;
    normalized = remove_failure_label(normalized);
    for (auto& value : normalized)
    {
        if (value == 'O' || value == 'o') value = '0';
        else if (value == 'l' || value == 'I') value = '1';
    }

    auto first = normalized.begin();
    auto last = normalized.end();
    while (first != last && ascii_space(*first)) ++first;
    while (first != last && ascii_space(*(last - 1))) --last;
    if (std::none_of(first, last, ascii_digit)) return std::nullopt;

    auto cursor = first;
    bool const percent = maximum == 100;
    if (!percent)
    {
        if (cursor == last || (*cursor != '+' && *cursor != '*' && *cursor != '#')) return std::nullopt;
        ++cursor;
        while (cursor != last && ascii_space(*cursor)) ++cursor;
    }

    int value = 0;
    int digit_count = 0;
    while (cursor != last && ascii_digit(*cursor))
    {
        if (++digit_count > (percent ? 3 : 4)) return std::nullopt;
        value = value * 10 + (*cursor - '0');
        ++cursor;
    }
    if (digit_count == 0) return std::nullopt;
    if (percent)
    {
        while (cursor != last && ascii_space(*cursor)) ++cursor;
        if (cursor == last || *cursor != '%') return std::nullopt;
        ++cursor;
    }
    if (cursor != last || value > maximum) return std::nullopt;
    return value;
}

[[nodiscard]] int count_digits(int value) noexcept
{
    int count = 1;
    while (value >= 10)
    {
        value /= 10;
        ++count;
    }
    return count;
}

[[nodiscard]] std::map<int, std::string> parse_tsv_pages(std::string const& tsv, std::size_t page_count)
{
    std::map<int, std::vector<std::string>> words_by_page;
    std::size_t line_start = 0;
    while (line_start < tsv.size())
    {
        auto line_end = tsv.find('\n', line_start);
        if (line_end == std::string::npos) line_end = tsv.size();
        auto line = std::string_view(tsv).substr(line_start, line_end - line_start);
        if (!line.empty() && line.back() == '\r') line.remove_suffix(1);

        std::array<std::string_view, 12> cells{};
        std::size_t cell_start = 0;
        std::size_t cell_count = 0;
        while (cell_count < cells.size() - 1)
        {
            auto const tab = line.find('\t', cell_start);
            if (tab == std::string_view::npos)
                break;
            cells[cell_count++] = line.substr(cell_start, tab - cell_start);
            cell_start = tab + 1;
        }
        if (cell_count == cells.size() - 1)
            cells[cell_count++] = line.substr(cell_start);
        if (cell_count == cells.size() && cells[0] == "5")
        {
            int page = 0;
            bool const parsed = !cells[1].empty()
                && std::all_of(cells[1].begin(), cells[1].end(), ascii_digit);
            if (parsed)
            {
                for (char digit : cells[1])
                {
                    if (page > 100000) { page = 0; break; }
                    page = page * 10 + (digit - '0');
                }
            }
            if (parsed && page > 0 && static_cast<std::size_t>(page) <= page_count && !is_blank_utf8(cells[11]))
                words_by_page[page].emplace_back(cells[11]);
        }
        line_start = line_end == tsv.size() ? tsv.size() : line_end + 1;
    }

    std::map<int, std::string> pages;
    for (auto& [page, words] : words_by_page)
    {
        std::string joined;
        for (auto const& word : words)
        {
            if (!joined.empty()) joined.push_back(' ');
            joined += word;
        }
        pages.emplace(page, std::move(joined));
    }
    return pages;
}

[[nodiscard]] std::wstring build_tesseract_command(
    std::filesystem::path const& executable,
    std::filesystem::path const& input_list,
    std::string const& psm)
{
    return build_windows_command_line(executable,
        {utf8_path(input_list), "stdout", "--psm", psm, "-l", "eng", "tsv"});
}

void append_reading(NumericOcrBatchReading& reading, std::string const& raw)
{
    if (!reading.rawText.empty()) reading.rawText += " | ";
    reading.rawText += raw;
}

void throw_if_cancelled(std::stop_token cancellation)
{
    if (cancellation.stop_requested())
        throw std::system_error(std::make_error_code(std::errc::operation_canceled), "Numeric OCR was canceled.");
}

} // namespace

NumericOcrBatch::NumericOcrBatch()
    : owned_process_(create_win32_process()), process_(owned_process_.get()), executable_(locate_executable())
{
}

NumericOcrBatch::NumericOcrBatch(IWin32Process& process, std::filesystem::path executable)
    : process_(&process), executable_(std::move(executable))
{
}

NumericOcrBatch::~NumericOcrBatch() = default;
NumericOcrBatch::NumericOcrBatch(NumericOcrBatch&&) noexcept = default;
NumericOcrBatch& NumericOcrBatch::operator=(NumericOcrBatch&&) noexcept = default;

std::filesystem::path NumericOcrBatch::locate_executable()
{
    std::array<wchar_t, 32768> buffer{};
    auto const path_length = ::GetEnvironmentVariableW(L"PATH", buffer.data(), static_cast<DWORD>(buffer.size()));
    if (path_length > 0 && path_length < buffer.size())
    {
        std::wstring path_value(buffer.data(), path_length);
        std::size_t start = 0;
        while (start <= path_value.size())
        {
            auto end = path_value.find(L';', start);
            if (end == std::wstring::npos) end = path_value.size();
            auto directory = path_value.substr(start, end - start);
            if (directory.size() >= 2 && directory.front() == L'"' && directory.back() == L'"')
                directory = directory.substr(1, directory.size() - 2);
            if (!directory.empty())
            {
                auto candidate = std::filesystem::path(directory) / L"tesseract.exe";
                std::error_code error;
                if (std::filesystem::is_regular_file(candidate, error)) return candidate;
            }
            if (end == path_value.size()) break;
            start = end + 1;
        }
    }

    for (auto const variable : {L"ProgramFiles", L"ProgramFiles(x86)"})
    {
        auto const length = ::GetEnvironmentVariableW(variable, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (length == 0 || length >= buffer.size()) continue;
        auto candidate = std::filesystem::path(std::wstring(buffer.data(), length))
            / L"Tesseract-OCR" / L"tesseract.exe";
        std::error_code error;
        if (std::filesystem::is_regular_file(candidate, error)) return candidate;
    }
    return {};
}

NumericOcrBatchResult NumericOcrBatch::read(
    std::span<NumericOcrBatchField const> fields,
    std::stop_token cancellation) const
{
    throw_if_cancelled(cancellation);
    NumericOcrBatchResult readings;
    std::set<std::string, std::less<>> keys;
    for (auto const& field : fields)
    {
        if (field.key.empty() || !keys.insert(field.key).second)
            throw std::invalid_argument("Numeric OCR field keys must be nonempty and unique.");
        if (field.maxValue < 0)
            throw std::invalid_argument("Numeric OCR maximum values must be nonnegative.");
        if (field.crop.width <= 0 || field.crop.height <= 0 || field.crop.width > 10000 || field.crop.height > 10000
            || field.crop.gray.size() != static_cast<std::size_t>(field.crop.width) * field.crop.height)
            throw std::invalid_argument("Numeric OCR requires valid grayscale crops.");
        readings.emplace(field.key, NumericOcrBatchReading{});
    }
    if (fields.empty() || process_ == nullptr || executable_.empty()) return readings;

    auto const deadline = Clock::now() + frame_budget;
    try
    {
        ComApartment apartment;
        TemporaryBatchDirectory temporary;
        std::vector<std::filesystem::path> normal_images(fields.size());
        std::vector<std::filesystem::path> padded_images(fields.size());
        for (std::size_t index = 0; index < fields.size(); ++index)
        {
            throw_if_cancelled(cancellation);
            auto const image = resize_nearest(fields[index].crop, false);
            normal_images[index] = temporary.path() / ("image-" + std::to_string(index) + ".png");
            save_png(normal_images[index], image);
        }

        struct Mode { char const* psm; bool padded; };
        constexpr std::array modes{Mode{"7", false}, Mode{"8", false}, Mode{"13", false}, Mode{"7", true}};
        for (auto const mode : modes)
        {
            throw_if_cancelled(cancellation);
            if (Clock::now() >= deadline) break;

            std::vector<std::size_t> pending;
            std::vector<std::filesystem::path> pending_images;
            for (std::size_t index = 0; index < fields.size(); ++index)
            {
                if (readings.at(fields[index].key).value.has_value()) continue;
                if (mode.padded && !fields[index].validateGainGlyphs) continue;
                if (mode.padded)
                {
                    if (padded_images[index].empty())
                    {
                        auto const image = resize_nearest(fields[index].crop, true);
                        padded_images[index] = temporary.path()
                            / ("image-" + std::to_string(index) + "-padded.png");
                        save_png(padded_images[index], image);
                    }
                    pending_images.push_back(padded_images[index]);
                }
                else
                {
                    pending_images.push_back(normal_images[index]);
                }
                pending.push_back(index);
            }
            if (pending.empty() || Clock::now() >= deadline) continue;

            auto const list_path = temporary.path() / "batch.txt";
            write_image_list(list_path, pending_images);
            auto const remaining = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Clock::now());
            if (remaining <= std::chrono::milliseconds::zero()) break;
            auto const result = process_->execute(executable_,
                build_tesseract_command(executable_, list_path, mode.psm), cancellation,
                remaining, max_tsv_bytes, false);
            throw_if_cancelled(cancellation);
            if (result.canceled)
                throw std::system_error(std::make_error_code(std::errc::operation_canceled),
                    "The numeric OCR process was canceled.");
            if (result.timed_out || Clock::now() >= deadline) break;
            if (!result.started || result.exit_code != 0 || result.standard_output_truncated)
                continue;

            auto const pages = parse_tsv_pages(result.standard_output, pending.size());
            for (std::size_t page = 0; page < pending.size(); ++page)
            {
                auto const index = pending[page];
                auto const found = pages.find(static_cast<int>(page + 1));
                std::string const raw = found == pages.end() ? std::string{} : found->second;
                auto& reading = readings.at(fields[index].key);
                append_reading(reading, raw);

                auto value = parse_numeric(raw, fields[index].maxValue);
                bool glyph_mismatch = false;
                if (value && fields[index].validateGainGlyphs)
                {
                    int const groups = Vision::count_ink_groups(fields[index].crop, 128);
                    glyph_mismatch = groups != 1 + count_digits(*value);
                    if (glyph_mismatch) value.reset();
                }
                reading.glyphMismatch = reading.glyphMismatch || glyph_mismatch;
                reading.value = value;
            }
        }
    }
    catch (std::system_error const& exception)
    {
        if (exception.code() == std::make_error_code(std::errc::operation_canceled)) throw;
        // Tesseract is an optional fallback. Unavailable process/image services
        // leave the affected OCR values unknown, matching the managed behavior.
    }
    catch (...)
    {
        // Optional OCR must never turn an image-processing failure into a guessed value.
    }
    throw_if_cancelled(cancellation);
    return readings;
}

} // namespace UmaAssistant::Recognition
