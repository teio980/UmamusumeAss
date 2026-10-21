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

std::filesystem::path locate_tessdata_path()
{
    char const* env = std::getenv("TESSDATA_PREFIX");
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
            return;
        }

        api.SetVariable("debug_file", "/dev/null");
        api.SetPageSegMode(tesseract::PSM_AUTO);
        initialized = true;
        current_language = lang;
    }
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

    if (image.rgba.size() == expected_rgba)
    {
        impl_->api.SetImage(image.rgba.data(), image.width, image.height, 4, image.width * 4);
    }
    else if (image.gray.size() == expected_gray)
    {
        impl_->api.SetImage(image.gray.data(), image.width, image.height, 1, image.width);
    }

    if (impl_->api.Recognize(nullptr) != 0)
    {
        impl_->api.Clear();
        return result;
    }

    std::unique_ptr<tesseract::ResultIterator> ri(impl_->api.GetIterator());
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
            result.detections.push_back(std::move(detection));
        } while (ri->Next(level));
    }

    impl_->api.Clear();
    return result;
}

} // namespace UmaAssistant::Recognition
