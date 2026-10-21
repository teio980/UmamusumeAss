#include "WicImageDecoder.hpp"

#include <CoreFoundation/CoreFoundation.h>
#include <CoreGraphics/CoreGraphics.h>
#include <ImageIO/ImageIO.h>
#include <Accelerate/Accelerate.h>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <stdexcept>
#include <string>

namespace UmaAssistant::Resources::WicImageDecoder {
namespace {

constexpr std::uint32_t max_dimension = 10000;
constexpr std::uint64_t max_pixels = 100'000'000;

[[nodiscard]] std::uint64_t checked_pixel_count(std::uint32_t width, std::uint32_t height)
{
    auto const pixel_count = static_cast<std::uint64_t>(width) * height;
    if (width == 0 || height == 0 || width > max_dimension || height > max_dimension
        || pixel_count > max_pixels)
    {
        throw std::runtime_error("Image dimensions exceed supported limits");
    }
    return pixel_count;
}

[[nodiscard]] Vision::GrayImage make_rgba_image(
    std::uint32_t width,
    std::uint32_t height,
    std::span<std::uint8_t const> rgba)
{
    auto const pixel_count = checked_pixel_count(width, height);
    auto const rgba_size = pixel_count * 4u;
    if (rgba.size() != rgba_size)
    {
        throw std::invalid_argument("RGBA image byte length does not match its dimensions");
    }

    Vision::GrayImage image;
    image.width = static_cast<std::int32_t>(width);
    image.height = static_cast<std::int32_t>(height);
    image.rgba.assign(rgba.begin(), rgba.end());
    image.gray.resize(static_cast<std::size_t>(pixel_count));
    for (std::size_t index = 0; index < image.gray.size(); ++index)
    {
        auto const offset = index * 4;
        image.gray[index] = static_cast<std::uint8_t>(
            (static_cast<std::uint32_t>(image.rgba[offset]) * 299u
                + static_cast<std::uint32_t>(image.rgba[offset + 1]) * 587u
                + static_cast<std::uint32_t>(image.rgba[offset + 2]) * 114u) / 1000u);
    }
    return image;
}

[[nodiscard]] Vision::GrayImage decode_cg_image(CGImageRef image)
{
    auto const width = static_cast<std::uint32_t>(CGImageGetWidth(image));
    auto const height = static_cast<std::uint32_t>(CGImageGetHeight(image));
    auto const pixel_count = checked_pixel_count(width, height);

    Vision::GrayImage result;
    result.width = static_cast<std::int32_t>(width);
    result.height = static_cast<std::int32_t>(height);
    result.rgba.resize(static_cast<std::size_t>(pixel_count * 4u));
    result.gray.resize(static_cast<std::size_t>(pixel_count));

    CGColorSpaceRef colorSpace = CGColorSpaceCreateDeviceRGB();
    if (!colorSpace)
    {
        throw std::runtime_error("Failed to create device RGB color space");
    }

    CGContextRef ctx = CGBitmapContextCreate(
        result.rgba.data(),
        width,
        height,
        8,
        width * 4u,
        colorSpace,
        static_cast<CGBitmapInfo>(static_cast<uint32_t>(kCGImageAlphaPremultipliedLast) | static_cast<uint32_t>(kCGBitmapByteOrder32Big)));

    if (!ctx)
    {
        CGColorSpaceRelease(colorSpace);
        throw std::runtime_error("Failed to create bitmap context");
    }

    CGContextDrawImage(ctx, CGRectMake(0, 0, width, height), image);
    CGContextRelease(ctx);
    CGColorSpaceRelease(colorSpace);

    // Unpremultiply alpha back to straight RGBA
    vImage_Buffer vbuf = {
        result.rgba.data(),
        height,
        width,
        static_cast<std::size_t>(width * 4u)
    };
    vImageUnpremultiplyData_RGBA8888(&vbuf, &vbuf, kvImageNoFlags);

    // Derive grayscale plane using standard BT.601 integer formula
    for (std::size_t index = 0; index < result.gray.size(); ++index)
    {
        auto const offset = index * 4;
        result.gray[index] = static_cast<std::uint8_t>(
            (static_cast<std::uint32_t>(result.rgba[offset]) * 299u
                + static_cast<std::uint32_t>(result.rgba[offset + 1]) * 587u
                + static_cast<std::uint32_t>(result.rgba[offset + 2]) * 114u) / 1000u);
    }

    return result;
}

[[nodiscard]] std::filesystem::path path_from_utf8(std::string_view value)
{
    std::u8string utf8;
    utf8.reserve(value.size());
    for (char const byte : value)
    {
        utf8.push_back(static_cast<char8_t>(static_cast<unsigned char>(byte)));
    }
    return std::filesystem::path(utf8);
}

} // namespace

Vision::GrayImage decode_file(std::filesystem::path const& path)
{
    std::string const u8path = path.string();
    if (u8path.empty())
    {
        throw std::invalid_argument("Image path cannot be empty");
    }

    CFURLRef url = CFURLCreateFromFileSystemRepresentation(
        kCFAllocatorDefault,
        reinterpret_cast<UInt8 const*>(u8path.data()),
        static_cast<CFIndex>(u8path.size()),
        false);
    if (!url)
    {
        throw std::runtime_error("Failed to create URL for path: " + u8path);
    }

    CGImageSourceRef source = CGImageSourceCreateWithURL(url, nullptr);
    CFRelease(url);
    if (!source || CGImageSourceGetCount(source) == 0)
    {
        if (source) CFRelease(source);
        throw std::runtime_error("Failed to read image from path: " + u8path);
    }

    CGImageRef image = CGImageSourceCreateImageAtIndex(source, 0, nullptr);
    CFRelease(source);
    if (!image)
    {
        throw std::runtime_error("Failed to decode image from path: " + u8path);
    }

    try
    {
        Vision::GrayImage result = decode_cg_image(image);
        CFRelease(image);
        return result;
    }
    catch (...)
    {
        CFRelease(image);
        throw;
    }
}

Vision::GrayImage decode_file_utf8(std::string_view path)
{
    if (path.empty())
    {
        throw std::invalid_argument("Image path cannot be empty");
    }
    return decode_file(path_from_utf8(path));
}

Vision::GrayImage decode_bytes(std::span<std::uint8_t const> encoded)
{
    if (encoded.empty())
    {
        throw std::invalid_argument("Encoded image bytes cannot be empty");
    }

    CFDataRef data = CFDataCreateWithBytesNoCopy(
        kCFAllocatorDefault,
        encoded.data(),
        static_cast<CFIndex>(encoded.size()),
        kCFAllocatorNull);
    if (!data)
    {
        throw std::runtime_error("Failed to wrap encoded bytes in CFData");
    }

    CGImageSourceRef source = CGImageSourceCreateWithData(data, nullptr);
    CFRelease(data);
    if (!source || CGImageSourceGetCount(source) == 0)
    {
        if (source) CFRelease(source);
        throw std::runtime_error("Failed to create image source from bytes");
    }

    CGImageRef image = CGImageSourceCreateImageAtIndex(source, 0, nullptr);
    CFRelease(source);
    if (!image)
    {
        throw std::runtime_error("Failed to decode image from bytes");
    }

    try
    {
        Vision::GrayImage result = decode_cg_image(image);
        CFRelease(image);
        return result;
    }
    catch (...)
    {
        CFRelease(image);
        throw;
    }
}

Vision::GrayImage from_rgba(
    std::int32_t width,
    std::int32_t height,
    std::span<std::uint8_t const> rgba)
{
    if (width <= 0 || height <= 0)
    {
        throw std::invalid_argument("RGBA image dimensions must be positive");
    }
    return make_rgba_image(
        static_cast<std::uint32_t>(width),
        static_cast<std::uint32_t>(height),
        rgba);
}

} // namespace UmaAssistant::Resources::WicImageDecoder
