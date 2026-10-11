#pragma once

#include "TemplateMatcher.hpp"

#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace UmaAssistant::Recognition {

struct TextRect
{
    int x{};
    int y{};
    int width{};
    int height{};
};

struct TextDetection
{
    std::string text;
    TextRect bounds;
    double confidence{1.0};
};

struct TextRecognitionResult
{
    std::vector<TextDetection> detections;
    std::string language;
    int width{};
    int height{};
};

class WindowsOcrRecognizer
{
public:
    WindowsOcrRecognizer();
    ~WindowsOcrRecognizer();
    WindowsOcrRecognizer(WindowsOcrRecognizer&&) noexcept;
    WindowsOcrRecognizer& operator=(WindowsOcrRecognizer&&) noexcept;
    WindowsOcrRecognizer(WindowsOcrRecognizer const&) = delete;
    WindowsOcrRecognizer& operator=(WindowsOcrRecognizer const&) = delete;

    [[nodiscard]] TextRecognitionResult recognize(
        Vision::GrayImage const& image,
        std::string_view requested_language = {});

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

}
