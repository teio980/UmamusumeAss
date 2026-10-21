#include "ImageTransforms.hpp"
#include "WicImageDecoder.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <stdexcept>
#include <vector>
#if defined(_MSC_VER)
#include <intrin.h>
#endif

namespace UmaAssistant::Resources {
namespace {
using Pixel = std::array<float, 4>;
struct Kernel { int start; std::vector<float> weights; };

bool grouped_fma_available() noexcept
{
#if defined(_MSC_VER) && defined(_M_X64)
    int features[4]{};
    __cpuid(features, 1);
    if ((features[2] & (1 << 12)) == 0 || (features[2] & (1 << 27)) == 0
        || (_xgetbv(0) & 6) != 6) return false;
    __cpuidex(features, 7, 0);
    return (features[1] & (1 << 5)) != 0;
#else
    return false;
#endif
}

int byte_pack_block() noexcept
{
#if defined(_MSC_VER) && defined(_M_X64)
    int features[4]{};
    __cpuid(features, 1);
    if ((features[2] & (1 << 27)) != 0 && (_xgetbv(0) & 6) == 6)
    {
        __cpuidex(features, 7, 0);
        if ((features[1] & (1 << 5)) != 0) return 32;
    }
    return 16;
#else
    return 0;
#endif
}

float round_even(float value) noexcept
{
    float const lower = std::floor(value);
    float const fraction = value - lower;
    if (fraction < 0.5f) return lower;
    if (fraction > 0.5f) return lower + 1.0f;
    return static_cast<int>(lower) % 2 == 0 ? lower : lower + 1.0f;
}

float cubic(float distance) noexcept
{
    float const x = std::abs(distance);
    if (x <= 1.0f) return ((1.5f * x - 2.5f) * x * x) + 1.0f;
    if (x < 2.0f) return (((-0.5f * x + 2.5f) * x - 4.0f) * x) + 2.0f;
    return 0.0f;
}

std::vector<Kernel> kernels(int source, int target)
{
    double const ratio = static_cast<double>(source) / target;
    double const scale = std::max(1.0, ratio);
    int const radius = static_cast<int>(std::ceil(scale * 2.0 - 1e-8));
    std::vector<Kernel> result;
    result.reserve(static_cast<std::size_t>(target));
    for (int index = 0; index < target; ++index)
    {
        double const center = (index + 0.5) * ratio - 0.5;
        int const left = std::max(0, static_cast<int>(std::ceil(center - radius - 1e-8)));
        int const right = std::min(source - 1, static_cast<int>(std::floor(center + radius + 1e-8)));
        Kernel kernel{left, {}};
        std::vector<double> values;
        double sum = 0;
        for (int point = left; point <= right; ++point)
        {
            double const value = static_cast<double>(cubic(static_cast<float>((point - center) / scale)));
            values.push_back(value);
            sum += value;
        }
        for (auto value : values)
            kernel.weights.push_back(static_cast<float>(sum > 0 ? value / sum : value));
        result.push_back(std::move(kernel));
    }
    return result;
}

template<class At>
Pixel convolve(Kernel const& kernel, At const& at)
{
    static bool const grouped = grouped_fma_available();
    Pixel result{};
    if (!grouped)
    {
        for (std::size_t index = 0; index < kernel.weights.size(); ++index)
            for (std::size_t channel = 0; channel < 4; ++channel)
                result[channel] += at(kernel.start + static_cast<int>(index))[channel] * kernel.weights[index];
        return result;
    }
    // Independent even/odd accumulators, with four-tap unrolling. Keep each
    // reduction at float precision; using a double sum changes edge pixels.
    std::array<Pixel, 4> sums{};
    std::size_t index = 0;
    for (; index + 4 <= kernel.weights.size(); index += 4)
        for (std::size_t lane = 0; lane < 4; ++lane)
            for (std::size_t channel = 0; channel < 4; ++channel)
                sums[lane][channel] = std::fma(at(kernel.start + static_cast<int>(index + lane))[channel],
                    kernel.weights[index + lane], sums[lane][channel]);
    for (std::size_t channel = 0; channel < 4; ++channel)
    {
        sums[0][channel] += sums[2][channel];
        sums[1][channel] += sums[3][channel];
    }
    if (index + 2 <= kernel.weights.size())
    {
        for (std::size_t lane = 0; lane < 2; ++lane)
            for (std::size_t channel = 0; channel < 4; ++channel)
                sums[lane][channel] = std::fma(at(kernel.start + static_cast<int>(index + lane))[channel],
                    kernel.weights[index + lane], sums[lane][channel]);
        index += 2;
    }
    for (std::size_t channel = 0; channel < 4; ++channel)
    {
        result[channel] = sums[0][channel] + sums[1][channel];
        if (index < kernel.weights.size())
            result[channel] = std::fma(at(kernel.start + static_cast<int>(index))[channel],
                kernel.weights[index], result[channel]);
    }
    return result;
}

void validate(Vision::GrayImage const& image)
{
    if (image.width <= 0 || image.height <= 0 || image.width > 10000 || image.height > 10000
        || image.gray.size() != static_cast<std::size_t>(image.width) * image.height
        || (!image.rgba.empty() && image.rgba.size() != image.gray.size() * 4u))
        throw std::invalid_argument("Invalid image planes");
}
}

Vision::GrayImage crop_image(Vision::GrayImage const& image, int x, int y, int width, int height)
{
    validate(image);
    if (x < 0 || y < 0 || width <= 0 || height <= 0
        || static_cast<std::int64_t>(x) + width > image.width
        || static_cast<std::int64_t>(y) + height > image.height)
        throw std::invalid_argument("Crop is outside the image");
    Vision::GrayImage result;
    result.width = width;
    result.height = height;
    result.gray.resize(static_cast<std::size_t>(width) * height);
    if (!image.rgba.empty()) result.rgba.resize(result.gray.size() * 4u);
    for (int row = 0; row < height; ++row)
    {
        auto const source = static_cast<std::size_t>(y + row) * image.width + x;
        auto const destination = static_cast<std::size_t>(row) * width;
        std::copy_n(image.gray.begin() + static_cast<std::ptrdiff_t>(source), width,
            result.gray.begin() + static_cast<std::ptrdiff_t>(destination));
        if (!image.rgba.empty())
            std::copy_n(image.rgba.begin() + static_cast<std::ptrdiff_t>(source * 4u), width * 4,
                result.rgba.begin() + static_cast<std::ptrdiff_t>(destination * 4u));
    }
    return result;
}

Vision::GrayImage resize_image(Vision::GrayImage const& image, int width, int height, bool mean_gray)
{
    validate(image);
    if (width <= 0 || height <= 0 || width > 10000 || height > 10000)
        throw std::invalid_argument("Resize dimensions are outside supported limits");
    std::vector<std::uint8_t> rgba(static_cast<std::size_t>(width) * height * 4u);
    if (width == image.width && height == image.height && !image.rgba.empty()) rgba = image.rgba;
    else
    {
        int const pack_block = byte_pack_block();
        int const packed_bytes = pack_block == 0 ? 0 : width * 4 / pack_block * pack_block;
        auto const horizontal = kernels(image.width, width);
        auto const vertical = kernels(image.height, height);
        std::vector<Pixel> first_pass(static_cast<std::size_t>(width) * image.height);
        constexpr float unit = 1.0f / 255.0f;
        for (int row = 0; row < image.height; ++row)
            for (int column = 0; column < width; ++column)
                first_pass[static_cast<std::size_t>(row) * width + column] = convolve(horizontal[column], [&](int x)
                {
                    auto const index = static_cast<std::size_t>(row) * image.width + x;
                    if (image.rgba.empty())
                    {
                        float const gray = image.gray[index] * unit;
                        return Pixel{gray, gray, gray, 1.0f};
                    }
                    auto const offset = index * 4u;
                    float const alpha = image.rgba[offset + 3] * unit;
                    return Pixel{image.rgba[offset] * unit * alpha, image.rgba[offset + 1] * unit * alpha,
                        image.rgba[offset + 2] * unit * alpha, alpha};
                });
        for (int row = 0; row < height; ++row)
            for (int column = 0; column < width; ++column)
            {
                auto value = convolve(vertical[row], [&](int y) -> Pixel const&
                { return first_pass[static_cast<std::size_t>(y) * width + column]; });
                float const divisor = value[3] == 0.0f ? 1.0f : value[3];
                for (std::size_t channel = 0; channel < 3; ++channel) value[channel] /= divisor;
                auto const offset = (static_cast<std::size_t>(row) * width + column) * 4u;
                for (std::size_t channel = 0; channel < 4; ++channel)
                {
                    float const scaled = std::clamp(value[channel] * 255.0f, 0.0f, 255.0f);
                    // Bulk RGBA conversion rounds to even; the short row tail
                    // uses half-up conversion. Preserve both legacy behaviors.
                    bool const packed = column * 4 + static_cast<int>(channel) < packed_bytes;
                    rgba[offset + channel] = static_cast<std::uint8_t>(
                        packed ? round_even(scaled) : std::min(scaled + 0.5f, 255.0f));
                }
            }
    }
    auto result = WicImageDecoder::from_rgba(width, height, rgba);
    if (mean_gray)
        for (std::size_t index = 0; index < result.gray.size(); ++index)
            result.gray[index] = static_cast<std::uint8_t>((rgba[index * 4] + rgba[index * 4 + 1]
                + rgba[index * 4 + 2]) / 3);
    return result;
}

Vision::GrayImage resize_nearest(Vision::GrayImage const& image, int width, int height)
{
    validate(image);
    if (width <= 0 || height <= 0 || width > 10000 || height > 10000)
        throw std::invalid_argument("Resize dimensions are outside supported limits");
    Vision::GrayImage result{width, height, {}, {}};
    result.gray.resize(static_cast<std::size_t>(width) * height);
    if (!image.rgba.empty()) result.rgba.resize(result.gray.size() * 4);
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x)
        {
            auto const source = static_cast<std::size_t>(y * image.height / height) * image.width + x * image.width / width;
            auto const target = static_cast<std::size_t>(y) * width + x;
            result.gray[target] = image.gray[source];
            if (!image.rgba.empty())
                std::copy_n(image.rgba.begin() + static_cast<std::ptrdiff_t>(source * 4), 4,
                    result.rgba.begin() + static_cast<std::ptrdiff_t>(target * 4));
        }
    return result;
}

std::optional<PreparedTemplate> prepare_template(Vision::GrayImage const& image, TemplatePreparation preparation)
{
    validate(image);
    if (preparation == TemplatePreparation::original) return PreparedTemplate{image, preparation};
    if (image.rgba.empty()) throw std::invalid_argument("Template preparation requires RGBA pixels");
    int min_x = image.width, min_y = image.height, max_x = -1, max_y = -1;
    std::size_t opaque = 0;
    for (int y = 0; y < image.height; ++y)
        for (int x = 0; x < image.width; ++x)
        {
            auto const alpha = image.rgba[(static_cast<std::size_t>(y) * image.width + x) * 4u + 3];
            if (alpha >= 240) ++opaque;
            if (alpha < 24) continue;
            min_x = std::min(min_x, x); min_y = std::min(min_y, y);
            max_x = std::max(max_x, x); max_y = std::max(max_y, y);
        }
    if (max_x < min_x || max_y < min_y) return std::nullopt;
    if (preparation == TemplatePreparation::portrait)
    {
        if (opaque >= image.gray.size() * 0.98) return PreparedTemplate{image, TemplatePreparation::original};
        int const height = std::max(1, static_cast<int>(std::nearbyint((max_y - min_y + 1) * 0.78)));
        return PreparedTemplate{resize_image(crop_image(image, min_x, min_y, max_x - min_x + 1, height), 128, 160), preparation};
    }
    if (preparation != TemplatePreparation::head) throw std::invalid_argument("Unknown template preparation");
    int const bottom = std::min(max_y, min_y + std::max(1,
        static_cast<int>(std::nearbyint((max_y - min_y + 1) * 0.44))));
    int head_min = image.width, head_max = -1;
    for (int y = min_y; y <= bottom; ++y)
        for (int x = min_x; x <= max_x; ++x)
            if (image.rgba[(static_cast<std::size_t>(y) * image.width + x) * 4u + 3] >= 24)
            { head_min = std::min(head_min, x); head_max = std::max(head_max, x); }
    if (head_max < head_min) return std::nullopt;
    return PreparedTemplate{resize_image(crop_image(image, head_min, min_y,
        head_max - head_min + 1, bottom - min_y + 1), 128, 128), preparation};
}
}
