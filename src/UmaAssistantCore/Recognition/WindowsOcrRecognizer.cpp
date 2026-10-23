#include "WindowsOcrRecognizer.hpp"

#include <tesseract/baseapi.h>
#include <tesseract/ocrclass.h>

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdlib>
#include <filesystem>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#if defined(__APPLE__)
#include <dlfcn.h>
#include <mach-o/dyld.h>
#elif defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif

namespace UmaAssistant::Recognition {
namespace {

#if defined(__APPLE__)
bool IsUniformImage(const Vision::GrayImage& image)
{
    if (!image.rgba.empty())
    {
        for (std::size_t index = 4; index < image.rgba.size(); index += 4)
            if (image.rgba[index] != image.rgba[0] || image.rgba[index + 1] != image.rgba[1]
                || image.rgba[index + 2] != image.rgba[2]) return false;
        return true;
    }
    return std::all_of(image.gray.begin(), image.gray.end(), [&](const auto value) {
        return value == image.gray.front();
    });
}

bool SameTextLine(const TextDetection& left, const TextDetection& right)
{
    const int overlapX = (std::max)(0, (std::min)(left.bounds.x + left.bounds.width,
        right.bounds.x + right.bounds.width) - (std::max)(left.bounds.x, right.bounds.x));
    const int overlapY = (std::max)(0, (std::min)(left.bounds.y + left.bounds.height,
        right.bounds.y + right.bounds.height) - (std::max)(left.bounds.y, right.bounds.y));
    const int minWidth = (std::min)(left.bounds.width, right.bounds.width);
    const int minHeight = (std::min)(left.bounds.height, right.bounds.height);
    return minWidth > 0 && minHeight > 0 && overlapX * 2 >= minWidth
        && overlapY * 2 >= minHeight;
}

void MergeTextLines(std::vector<TextDetection>& lines, std::vector<TextDetection> extra,
    bool replaceExisting = true)
{
    for (auto& candidate : extra)
    {
        const auto existing = std::find_if(lines.begin(), lines.end(), [&](const auto& line) {
            return SameTextLine(line, candidate);
        });
        if (existing == lines.end())
            lines.push_back(std::move(candidate));
        else if (replaceExisting && candidate.confidence > existing->confidence)
            *existing = std::move(candidate);
    }
}
#endif

std::filesystem::path locate_tessdata_path()
{
#if defined(_MSC_VER)
    char* raw_env = nullptr;
    std::size_t env_size = 0;
    if (_dupenv_s(&raw_env, &env_size, "TESSDATA_PREFIX") != 0)
        raw_env = nullptr;
    std::unique_ptr<char, decltype(&std::free)> env_storage(raw_env, &std::free);
    char const* env = env_storage.get();
#else
    char const* env = std::getenv("TESSDATA_PREFIX");
#endif
    if (env && *env)
    {
        std::filesystem::path p(env);
        std::error_code ec;
        if (std::filesystem::is_directory(p, ec) && std::filesystem::exists(p / "eng.traineddata", ec))
            return p;
        if (std::filesystem::is_directory(p / "tessdata", ec) && std::filesystem::exists(p / "tessdata" / "eng.traineddata", ec))
            return p / "tessdata";
    }

    std::vector<std::filesystem::path> candidates;

    // Check application bundle and binary location on macOS
#if defined(__APPLE__)
    // A framework-dependent Avalonia launch runs inside dotnet. Resolve the
    // loaded library as well as the executable so packaged models do not rely
    // on the launcher's working directory or the original source checkout.
    static const char libraryAnchor{};
    Dl_info libraryInfo{};
    if (dladdr(&libraryAnchor, &libraryInfo) != 0 && libraryInfo.dli_fname)
    {
        for (auto cur = std::filesystem::path(libraryInfo.dli_fname).parent_path();
            !cur.empty(); cur = cur.parent_path())
        {
            candidates.push_back(cur / "resource" / "tessdata");
            candidates.push_back(cur / "tessdata");
            candidates.push_back(cur / "Resources" / "resource" / "tessdata");
            candidates.push_back(cur / "Resources" / "tessdata");
            if (cur == cur.parent_path()) break;
        }
    }
    char exe_buf[1024];
    uint32_t size = sizeof(exe_buf);
    if (_NSGetExecutablePath(exe_buf, &size) == 0)
    {
        std::filesystem::path exe_dir = std::filesystem::path(exe_buf).parent_path();
        for (std::filesystem::path cur = exe_dir; !cur.empty(); cur = cur.parent_path())
        {
            candidates.push_back(cur / "resource" / "tessdata");
            candidates.push_back(cur / "tessdata");
            candidates.push_back(cur / "Resources" / "resource" / "tessdata");
            candidates.push_back(cur / "Resources" / "tessdata");
            if (cur == cur.parent_path()) break;
        }
    }
#elif defined(_WIN32)
    std::array<wchar_t, 32768> buffer{};
    auto const len = ::GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (len > 0 && len < buffer.size())
    {
        std::filesystem::path exe_dir = std::filesystem::path(std::wstring(buffer.data(), len)).parent_path();
        for (std::filesystem::path cur = exe_dir; !cur.empty(); cur = cur.parent_path())
        {
            candidates.push_back(cur / "resource" / "tessdata");
            candidates.push_back(cur / "tessdata");
            if (cur == cur.parent_path()) break;
        }
    }
#endif

    // Walk up from current working directory
    for (std::filesystem::path cur = std::filesystem::current_path(); !cur.empty(); cur = cur.parent_path())
    {
        candidates.push_back(cur / "resource" / "tessdata");
        candidates.push_back(cur / "tessdata");
        if (cur == cur.parent_path()) break;
    }

#if defined(CMAKE_SOURCE_DIR)
    candidates.push_back(std::filesystem::path(CMAKE_SOURCE_DIR) / "resource" / "tessdata");
#endif

    for (auto const& candidate : candidates)
    {
        std::error_code ec;
        if (std::filesystem::is_directory(candidate, ec) &&
            std::filesystem::exists(candidate / "eng.traineddata", ec))
        {
            return candidate;
        }
    }

    return std::filesystem::current_path() / "resource" / "tessdata";
}

} // namespace

struct WindowsOcrRecognizer::Impl
{
    tesseract::TessBaseAPI api;
    bool initialized = false;
    std::string current_language;
    std::mutex mutex;

    void ensure_initialized(std::string_view requested_language)
    {
        std::string lang = "eng";
        if (!requested_language.empty())
        {
            if (requested_language.starts_with("en") || requested_language.starts_with("EN"))
            {
                lang = "eng";
            }
        }

        if (initialized && current_language == lang)
        {
            return;
        }

        auto const data_path = locate_tessdata_path();
        int res = api.Init(data_path.string().c_str(), lang.c_str());
        if (res != 0 && lang != "eng")
        {
            res = api.Init(data_path.string().c_str(), "eng");
            if (res == 0) lang = "eng";
        }

        if (res != 0)
        {
            initialized = false;
#if defined(__APPLE__)
            throw std::runtime_error("Could not initialize Mac OCR with English model at "
                + data_path.string());
#endif
            return;
        }

        api.SetVariable("debug_file", "/dev/null");
        api.SetPageSegMode(tesseract::PSM_AUTO);
        initialized = true;
        current_language = lang;
    }

    std::vector<TextDetection> read_image(const Vision::GrayImage& image,
        tesseract::PageSegMode mode, int thresholdMethod);
};

WindowsOcrRecognizer::WindowsOcrRecognizer()
    : impl_(std::make_unique<Impl>())
{
}

WindowsOcrRecognizer::~WindowsOcrRecognizer() = default;
WindowsOcrRecognizer::WindowsOcrRecognizer(WindowsOcrRecognizer&&) noexcept = default;
WindowsOcrRecognizer& WindowsOcrRecognizer::operator=(WindowsOcrRecognizer&&) noexcept = default;

TextRecognitionResult WindowsOcrRecognizer::recognize(
    Vision::GrayImage const& image,
    std::string_view requested_language)
{
    TextRecognitionResult result{};
    result.width = image.width;
    result.height = image.height;
    result.language = "eng";

    if (image.width <= 0 || image.height <= 0)
    {
        return result;
    }

    std::size_t const expected_rgba = static_cast<std::size_t>(image.width) * static_cast<std::size_t>(image.height) * 4;
    std::size_t const expected_gray = static_cast<std::size_t>(image.width) * static_cast<std::size_t>(image.height);

    bool const has_rgba = !image.rgba.empty();
    bool const has_gray = !image.gray.empty();

    if (has_rgba && image.rgba.size() != expected_rgba)
    {
        throw std::invalid_argument("Malformed RGBA plane buffer size");
    }
    if (has_gray && image.gray.size() != expected_gray)
    {
        throw std::invalid_argument("Malformed gray plane buffer size");
    }
    if (!has_rgba && !has_gray)
    {
        return result;
    }

    std::lock_guard<std::mutex> lock(impl_->mutex);
    impl_->ensure_initialized(requested_language);
    if (!impl_->initialized)
    {
        return result;
    }
    result.language = impl_->current_language;

#if defined(__APPLE__)
    // Single-line segmentation otherwise invents glyphs on blank UI panels.
    if (IsUniformImage(image)) return result;
#endif
    result.detections = impl_->read_image(image, tesseract::PSM_AUTO, 0);
#if defined(__APPLE__)
    // Global color Otsu can erase whole selected skill cards and white badge
    // lettering. A local Sauvola pass recovers those lines even when the first
    // pass found other text. Merge in source coordinates to avoid duplicate
    // rows, which would make skill selection ambiguous.
    if (image.width >= 8 && image.height >= 8)
        MergeTextLines(result.detections, impl_->read_image(image, tesseract::PSM_AUTO, 2));
    if (image.width >= 200 && image.height >= 200)
    {
        // UI grids have no paragraph layout. AUTO can omit an entire date or
        // button even when it confidently reads the surrounding cells. Sparse
        // segmentation contributes missing lines only; its word fragments must
        // not replace already recognized complete lines.
        auto sparse = impl_->read_image(image, tesseract::PSM_SPARSE_TEXT, 2);
        std::erase_if(sparse, [](const auto& line) {
            return line.confidence < 0.70 || std::none_of(line.text.begin(), line.text.end(), [](unsigned char ch) {
                return std::isalnum(ch) != 0;
            });
        });
        MergeTextLines(result.detections, std::move(sparse), false);
    }
    if (result.detections.empty() && image.height <= 180 && image.width <= 1200)
    {
        // A cropped digit or short label has no page layout to discover.
        MergeTextLines(result.detections, impl_->read_image(image, tesseract::PSM_SINGLE_LINE, 0));
    }
    std::stable_sort(result.detections.begin(), result.detections.end(), [](const auto& left, const auto& right) {
        return left.bounds.y == right.bounds.y ? left.bounds.x < right.bounds.x
            : left.bounds.y < right.bounds.y;
    });
#endif
    return result;
}

std::vector<TextDetection> WindowsOcrRecognizer::Impl::read_image(
    const Vision::GrayImage& image, tesseract::PageSegMode mode, int thresholdMethod)
{
    std::vector<TextDetection> detections;
    api.SetPageSegMode(mode);
    api.SetVariable("thresholding_method", std::to_string(thresholdMethod).c_str());
#if defined(__APPLE__)
    api.ClearAdaptiveClassifier();
#endif
    if (!image.rgba.empty())
    {
        api.SetImage(image.rgba.data(), image.width, image.height, 4, image.width * 4);
    }
    else
    {
        api.SetImage(image.gray.data(), image.width, image.height, 1, image.width);
    }

    if (api.Recognize(nullptr) != 0)
    {
        api.Clear();
#if defined(__APPLE__)
        throw std::runtime_error("Mac OCR recognition failed.");
#else
        return detections;
#endif
    }

    std::unique_ptr<tesseract::ResultIterator> ri(api.GetIterator());
    if (ri && !ri->Empty(tesseract::RIL_TEXTLINE))
    {
        tesseract::PageIteratorLevel const level = tesseract::RIL_TEXTLINE;
        do
        {
            std::unique_ptr<char[]> text(ri->GetUTF8Text(level));
            if (!text) continue;

            std::string line(text.get());
            while (!line.empty() && (line.back() == '\n' || line.back() == '\r' || line.back() == ' ' || line.back() == '\t'))
                line.pop_back();
            std::size_t start = 0;
            while (start < line.size() && (line[start] == ' ' || line[start] == '\t' || line[start] == '\r' || line[start] == '\n'))
                ++start;
            if (start > 0) line = line.substr(start);
            if (line.empty()) continue;

            int left = 0, top = 0, right = 0, bottom = 0;
            ri->BoundingBox(level, &left, &top, &right, &bottom);
            float const conf = ri->Confidence(level);

            TextDetection detection;
            detection.text = std::move(line);
            detection.bounds.x = left;
            detection.bounds.y = top;
            detection.bounds.width = right - left;
            detection.bounds.height = bottom - top;
            detection.confidence = static_cast<double>(std::clamp(conf, 0.0f, 100.0f)) / 100.0;
            detections.push_back(std::move(detection));
        } while (ri->Next(level));
    }

    api.Clear();
    return detections;
}

} // namespace UmaAssistant::Recognition
