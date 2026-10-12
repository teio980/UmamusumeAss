#include "WindowsOcrRecognizer.hpp"

#include <objbase.h>

#include <winrt/Windows.Globalization.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Media.Ocr.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/base.h>

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <future>
#include <limits>
#include <map>
#include <mutex>
#include <stdexcept>
#include <string>
#include <utility>

namespace UmaAssistant::Recognition {
namespace {

using namespace winrt::Windows::Globalization;
using namespace winrt::Windows::Graphics::Imaging;
using namespace winrt::Windows::Media::Ocr;
using namespace winrt::Windows::Storage::Streams;

[[nodiscard]] bool is_space(wchar_t value) noexcept
{
    auto const code = static_cast<std::uint32_t>(value);
    return (code >= 0x0009 && code <= 0x000D)
        || code == 0x0020 || code == 0x0085 || code == 0x00A0 || code == 0x1680
        || (code >= 0x2000 && code <= 0x200A) || code == 0x2028 || code == 0x2029
        || code == 0x202F || code == 0x205F || code == 0x3000;
}

[[nodiscard]] std::string trim_line(winrt::hstring const& text)
{
    auto value = std::wstring(text.c_str(), text.size());
    auto first = value.begin();
    auto last = value.end();
    while (first != last && is_space(*first)) ++first;
    while (first != last && is_space(*(last - 1))) --last;
    auto const trimmed = std::wstring(first, last);
    return winrt::to_string(winrt::hstring(trimmed.c_str(),
        static_cast<winrt::hstring::size_type>(trimmed.size())));
}

[[nodiscard]] std::string trim_request(std::string_view requested)
{
    auto const utf16 = std::wstring(winrt::to_hstring(requested));
    auto first = utf16.begin();
    auto last = utf16.end();
    while (first != last && is_space(*first)) ++first;
    while (first != last && is_space(*(last - 1))) --last;
    auto const trimmed = std::wstring(first, last);
    return winrt::to_string(winrt::hstring(trimmed.c_str(),
        static_cast<winrt::hstring::size_type>(trimmed.size())));
}

[[nodiscard]] std::string resolve_language(std::string_view requested, std::vector<std::string> const& available)
{
    auto const request = trim_request(requested);
    if (!request.empty())
    {
        for (auto const& language : available)
            if (_stricmp(language.c_str(), request.c_str()) == 0) return language;
        auto const base_start = request.find_first_not_of('-');
        if (base_start != std::string::npos)
        {
            auto const base_end = request.find('-', base_start);
            auto const base = request.substr(base_start, base_end - base_start);
            for (auto const& language : available)
            {
                auto const language_start = language.find_first_not_of('-');
                if (language_start == std::string::npos) continue;
                auto const language_end = language.find('-', language_start);
                if (_stricmp(language.substr(language_start, language_end - language_start).c_str(), base.c_str()) == 0)
                    return language;
            }
        }
    }
    for (auto const& language : available)
        if (language.size() >= 2 && _strnicmp(language.c_str(), "en", 2) == 0) return language;
    if (!available.empty()) return available.front();
    return request.empty() ? "en-US" : request;
}

class ApartmentScope
{
public:
    ApartmentScope()
    {
        const auto status = ::CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (status == RPC_E_CHANGED_MODE)
            return;
        winrt::check_hresult(status);
        initialized = true;
    }
    ApartmentScope(ApartmentScope const&) = delete;
    ApartmentScope& operator=(ApartmentScope const&) = delete;
    ~ApartmentScope() { if (initialized) ::CoUninitialize(); }
    [[nodiscard]] bool IsMta() const noexcept { return initialized; }

private:
    bool initialized{};
};

// C++/WinRT caches activation factories across recognizer instances. Keep the
// MTA alive across short-lived calls and worker threads until those factories
// are released, instead of tearing it down between OCR operations.
class OcrRuntimeLifetime
{
public:
    OcrRuntimeLifetime() { winrt::check_hresult(::CoIncrementMTAUsage(&m_cookie)); }
    ~OcrRuntimeLifetime()
    {
        winrt::clear_factory_cache();
        if (m_cookie) static_cast<void>(::CoDecrementMTAUsage(m_cookie));
    }
private:
    CO_MTA_USAGE_COOKIE m_cookie{};
};

void EnsureOcrRuntime()
{
    static OcrRuntimeLifetime lifetime;
}

[[nodiscard]] SoftwareBitmap make_bitmap(Vision::GrayImage const& image)
{
    if (image.width <= 0 || image.height <= 0 || image.width > 10000 || image.height > 10000)
        throw std::invalid_argument("Windows OCR image dimensions exceed 10000 pixels");
    auto const pixelCount = static_cast<std::size_t>(image.width) * image.height;
    auto const required = pixelCount * 4u;
    bool const grayOnly = image.rgba.empty() && image.gray.size() == pixelCount;
    if (!grayOnly && image.rgba.size() != required)
        throw std::invalid_argument("Windows OCR requires valid grayscale or straight RGBA8 pixels");
    if (required > (std::numeric_limits<std::uint32_t>::max)())
        throw std::invalid_argument("Windows OCR image buffer exceeds the WinRT buffer limit");

    Buffer buffer(static_cast<std::uint32_t>(required));
    buffer.Length(static_cast<std::uint32_t>(required));
    auto* destination = buffer.data();
    if (destination == nullptr)
        throw std::runtime_error("WinRT returned a null OCR input buffer");
    if (grayOnly)
        for (std::size_t index = 0; index < pixelCount; ++index)
        {
            auto const offset = index * 4u;
            destination[offset] = image.gray[index];
            destination[offset + 1] = image.gray[index];
            destination[offset + 2] = image.gray[index];
            destination[offset + 3] = 255;
        }
    else
        std::memcpy(destination, image.rgba.data(), required);
    return SoftwareBitmap::CreateCopyFromBuffer(buffer, BitmapPixelFormat::Rgba8,
        image.width, image.height, BitmapAlphaMode::Ignore);
}

}

struct WindowsOcrRecognizer::Impl
{
    struct Engine
    {
        explicit Engine(std::string const& tag)
            : value(OcrEngine::TryCreateFromLanguage(Language(winrt::to_hstring(tag))))
        {
            if (!value) throw std::runtime_error("Windows OCR language is not installed: " + tag);
        }
        OcrEngine value{nullptr};
        std::mutex gate;
    };

    std::mutex engines_gate;
    std::map<std::string, std::shared_ptr<Engine>, std::less<>> engines;

    [[nodiscard]] std::shared_ptr<Engine> get_engine(std::string const& tag)
    {
        std::lock_guard lock(engines_gate);
        auto const found = engines.find(tag);
        if (found != engines.end()) return found->second;
        auto engine = std::make_shared<Engine>(tag);
        engines.emplace(tag, engine);
        return engine;
    }
};

WindowsOcrRecognizer::WindowsOcrRecognizer() : impl_(std::make_unique<Impl>()) { EnsureOcrRuntime(); }
WindowsOcrRecognizer::~WindowsOcrRecognizer() = default;
WindowsOcrRecognizer::WindowsOcrRecognizer(WindowsOcrRecognizer&&) noexcept = default;
WindowsOcrRecognizer& WindowsOcrRecognizer::operator=(WindowsOcrRecognizer&&) noexcept = default;

TextRecognitionResult WindowsOcrRecognizer::recognize(
    Vision::GrayImage const& image,
    std::string_view requested_language)
{
    // Handles can be created and destroyed on different threads. Balance COM
    // initialization on the thread that actually performs recognition.
    ApartmentScope apartment;
    if (!apartment.IsMta())
    {
        // WinRT's synchronous async wait is forbidden on an STA caller (for
        // example a WPF dispatcher). Keep the public synchronous ABI while
        // performing recognition and its COM lifetime on an MTA worker.
        return std::async(std::launch::async, [&] {
            return recognize(image, requested_language);
        }).get();
    }
    std::vector<std::string> available;
    auto const language_view = OcrEngine::AvailableRecognizerLanguages();
    available.reserve(language_view.Size());
    for (std::uint32_t index = 0; index < language_view.Size(); ++index)
        available.push_back(winrt::to_string(language_view.GetAt(index).LanguageTag()));
    auto const selected = resolve_language(requested_language, available);
    auto const engine = impl_->get_engine(selected);
    auto bitmap = make_bitmap(image);
    auto const ocr = [&]() {
        std::lock_guard lock(engine->gate);
        return engine->value.RecognizeAsync(bitmap).get();
    }();

    TextRecognitionResult result;
    result.language = selected;
    result.width = image.width;
    result.height = image.height;
    auto const lines = ocr.Lines();
    result.detections.reserve(lines.Size());
    for (auto const& line : lines)
    {
        auto const text = trim_line(line.Text());
        auto const words = line.Words();
        if (text.empty() || words.Size() == 0) continue;
        double left = (std::numeric_limits<double>::max)();
        double top = (std::numeric_limits<double>::max)();
        double right = (std::numeric_limits<double>::lowest)();
        double bottom = (std::numeric_limits<double>::lowest)();
        for (auto const& word : words)
        {
            auto const bounds = word.BoundingRect();
            left = (std::min)(left, static_cast<double>(bounds.X));
            top = (std::min)(top, static_cast<double>(bounds.Y));
            right = (std::max)(right, static_cast<double>(bounds.X) + bounds.Width);
            bottom = (std::max)(bottom, static_cast<double>(bounds.Y) + bounds.Height);
        }
        auto const x = static_cast<int>(std::floor(left));
        auto const y = static_cast<int>(std::floor(top));
        auto const right_edge = static_cast<int>(std::ceil(right));
        auto const bottom_edge = static_cast<int>(std::ceil(bottom));
        result.detections.push_back({text,
            {std::clamp(x, 0, image.width), std::clamp(y, 0, image.height),
                std::clamp(right_edge - x, 0, image.width),
                std::clamp(bottom_edge - y, 0, image.height)},
            1.0});
    }
    return result;
}

}
