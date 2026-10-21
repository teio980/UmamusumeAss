#include "UmaAssistant/UmaCaller.h"

#include "CoreHandle.hpp"
#include "Core.hpp"
#include "CoreRuntime.hpp"
#include "Resources/WicImageDecoder.hpp"

#include <cstring>
#include <cstddef>
#include <limits>
#include <memory>
#include <span>
#include <string>
#include <utility>

struct UmaDecodedImageImpl
{
    std::shared_ptr<UmaAssistant::Resources::PreparedTemplate const> value;
};

namespace {

int32_t publish_decoded_image(
    UmaAssistant::Vision::GrayImage image,
    UmaDecodedImage* output)
{
    if (output == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    *output = nullptr;
    try
    {
        *output = new UmaDecodedImageImpl{std::make_shared<UmaAssistant::Resources::PreparedTemplate const>(
            UmaAssistant::Resources::PreparedTemplate{std::move(image)})};
        return UMA_SUCCESS;
    }
    catch (...)
    {
        return UMA_ERROR_RECOGNITION_FAILED;
    }
}

}

extern "C" {

UMA_API_PORT char const* UMA_CALL UmaGetVersion(void)
{
    try
    {
        return UmaAssistant::core_version();
    }
    catch (...)
    {
        return "";
    }
}

uint32_t UMA_API UmaGetApiVersion(void)
{
    return UMA_API_VERSION;
}

UmaHandle UMA_API UmaCreate(UmaApiCallback callback, void* custom_arg)
{
    try
    {
        return UmaAssistant::CoreApi::create_handle(callback, custom_arg);
    }
    catch (...)
    {
        return nullptr;
    }
}

void UMA_API UmaDestroy(UmaHandle handle)
{
    try
    {
        UmaAssistant::CoreApi::destroy_handle(handle);
    }
    catch (...)
    {
        return;
    }
}

int32_t UMA_API UmaSetUserDir(char const* utf8_path)
{
    try
    {
        if (utf8_path == nullptr || *utf8_path == '\0') return UMA_ERROR_INVALID_ARGUMENT;
        return UmaAssistant::CoreRuntime::instance().set_user_dir(utf8_path)
            ? UMA_SUCCESS
            : UMA_ERROR_INVALID_ARGUMENT;
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

int32_t UMA_API UmaLoadResource(char const* utf8_path)
{
    try
    {
        if (utf8_path == nullptr || *utf8_path == '\0') return UMA_ERROR_INVALID_ARGUMENT;
        return UmaAssistant::CoreRuntime::instance().load_resource(utf8_path)
            ? UMA_SUCCESS
            : UMA_ERROR_INVALID_ARGUMENT;
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

int32_t UMA_API UmaSetInstanceOptions(UmaHandle handle, char const* utf8_json)
{
    if (handle == nullptr || utf8_json == nullptr || *utf8_json == '\0')
        return UMA_ERROR_INVALID_ARGUMENT;
    return UmaAssistant::CoreApi::set_instance_options(handle, utf8_json);
}

UmaStartResult UMA_API UmaConnectAsync(
    UmaHandle handle, char const* adb_path, char const* serial, char const* profile)
{
    try
    {
        if (adb_path == nullptr || serial == nullptr || profile == nullptr)
        {
            return {0, UMA_ERROR_INVALID_ARGUMENT};
        }
        return UmaAssistant::CoreApi::start_connect(
            handle, std::string(adb_path), std::string(serial), std::string(profile));
    }
    catch (...)
    {
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    }
}

int32_t UMA_API UmaCancelConnect(UmaHandle handle, uint64_t operation_id)
{
    try
    {
        return UmaAssistant::CoreApi::cancel_operation(handle, operation_id);
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

int32_t UMA_API UmaCancelOperation(UmaHandle handle, uint64_t operation_id)
{
    try
    {
        return UmaAssistant::CoreApi::cancel_operation(handle, operation_id);
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

UmaStartResult UMA_API UmaVerifyGameAsync(UmaHandle handle, char const* utf8_package_id)
{
    try
    {
        if (handle == nullptr || utf8_package_id == nullptr || *utf8_package_id == '\0')
            return {0, UMA_ERROR_INVALID_ARGUMENT};
        return UmaAssistant::CoreApi::start_verify_game(handle, std::string(utf8_package_id));
    }
    catch (...) { return {0, UMA_ERROR_INVALID_ARGUMENT}; }
}

UmaStartResult UMA_API UmaCaptureAsync(UmaHandle handle)
{
    try { return UmaAssistant::CoreApi::start_capture(handle); }
    catch (...) { return {0, UMA_ERROR_INVALID_ARGUMENT}; }
}

int32_t UMA_API UmaGetFramePngSize(UmaHandle handle, uint64_t frame_id, uint64_t* size)
{
    try { return UmaAssistant::CoreApi::get_frame_png_size(handle, frame_id, size); }
    catch (...) { return UMA_ERROR_INVALID_ARGUMENT; }
}

int32_t UMA_API UmaCopyFramePng(UmaHandle handle, uint64_t frame_id, uint8_t* destination, uint64_t capacity)
{
    try { return UmaAssistant::CoreApi::copy_frame_png(handle, frame_id, destination, capacity); }
    catch (...) { return UMA_ERROR_INVALID_ARGUMENT; }
}

int32_t UMA_API UmaReleaseFrame(UmaHandle handle, uint64_t frame_id)
{
    try { return UmaAssistant::CoreApi::release_frame(handle, frame_id); }
    catch (...) { return UMA_ERROR_INVALID_ARGUMENT; }
}

UmaStartResult UMA_API UmaTapAsync(UmaHandle handle, uint64_t frame_id, int32_t x, int32_t y)
{
    try { return UmaAssistant::CoreApi::start_tap(handle, frame_id, x, y); }
    catch (...) { return {0, UMA_ERROR_INVALID_ARGUMENT}; }
}

UmaStartResult UMA_API UmaSwipeAsync(
    UmaHandle handle, uint64_t frame_id, int32_t x1, int32_t y1,
    int32_t x2, int32_t y2, int32_t duration_ms)
{
    try
    {
        return UmaAssistant::CoreApi::start_swipe(
            handle, frame_id, x1, y1, x2, y2, duration_ms, false);
    }
    catch (...) { return {0, UMA_ERROR_INVALID_ARGUMENT}; }
}

UmaStartResult UMA_API UmaSwipeWithOptionsAsync(
    UmaHandle handle, uint64_t frame_id, int32_t x1, int32_t y1,
    int32_t x2, int32_t y2, int32_t duration_ms, int32_t extra_swipe)
{
    if (extra_swipe != 0 && extra_swipe != 1)
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    try
    {
        return UmaAssistant::CoreApi::start_swipe(
            handle, frame_id, x1, y1, x2, y2, duration_ms, extra_swipe != 0);
    }
    catch (...) { return {0, UMA_ERROR_INVALID_ARGUMENT}; }
}

UmaStartResult UMA_API UmaTouchAsync(
    UmaHandle handle, uint64_t frame_id,
    int32_t canonical_x, int32_t canonical_y, int32_t phase)
{
    try
    {
        return UmaAssistant::CoreApi::start_touch(
            handle, frame_id, canonical_x, canonical_y, phase);
    }
    catch (...) { return {0, UMA_ERROR_INVALID_ARGUMENT}; }
}

UmaStartResult UMA_API UmaPrepareTouchAsync(UmaHandle handle, uint64_t frame_id)
{
    try
    {
        return UmaAssistant::CoreApi::start_prepare_touch(handle, frame_id);
    }
    catch (...) { return {0, UMA_ERROR_INVALID_ARGUMENT}; }
}

UmaStartResult UMA_API UmaKeyEventAsync(
    UmaHandle handle, uint64_t frame_id, char const* utf8_key_code)
{
    try
    {
        if (handle == nullptr || frame_id == 0 || utf8_key_code == nullptr || *utf8_key_code == '\0')
            return {0, UMA_ERROR_INVALID_ARGUMENT};
        return UmaAssistant::CoreApi::start_key_event(handle, frame_id, utf8_key_code);
    }
    catch (...) { return {0, UMA_ERROR_INVALID_ARGUMENT}; }
}

UmaStartResult UMA_API UmaInputTextAsync(
    UmaHandle handle, uint64_t frame_id, char const* utf8_text)
{
    try
    {
        if (handle == nullptr || frame_id == 0 || utf8_text == nullptr || *utf8_text == '\0')
            return {0, UMA_ERROR_INVALID_ARGUMENT};
        return UmaAssistant::CoreApi::start_input_text(handle, frame_id, utf8_text);
    }
    catch (...) { return {0, UMA_ERROR_INVALID_ARGUMENT}; }
}

UmaStartResult UMA_API UmaPackageOperationAsync(
    UmaHandle handle,
    char const* utf8_package_id,
    char const* utf8_activity,
    int32_t operation)
{
    if (handle == nullptr || utf8_package_id == nullptr || *utf8_package_id == '\0'
        || operation < UMA_PACKAGE_START || operation > UMA_PACKAGE_QUERY_RUNNING
        || (operation == UMA_PACKAGE_START_ACTIVITY
            && (utf8_activity == nullptr || *utf8_activity == '\0')))
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    try
    {
        return UmaAssistant::CoreApi::start_package_operation(
            handle,
            std::string{utf8_package_id},
            utf8_activity == nullptr ? std::string{} : std::string{utf8_activity},
            operation);
    }
    catch (...) { return {0, UMA_ERROR_INVALID_ARGUMENT}; }
}

int32_t UMA_API UmaPrepareNumericGlyphs(UmaImageView const* source, int32_t warm_gain,
    UmaColorRule const* rule, UmaDecodedImage* image)
{
    if (!image) return UMA_ERROR_INVALID_ARGUMENT;
    *image = nullptr;
    UmaAssistant::Vision::GrayImage pixels;
    auto const result = UmaAssistant::CoreApi::prepare_numeric_glyphs(source, warm_gain, rule, pixels);
    return result == UMA_SUCCESS && pixels.width > 0 ? publish_decoded_image(std::move(pixels), image) : result;
}
int32_t UMA_API UmaComposeGrayImages(UmaImageView const* sources, UmaImageRect const* placements,
    uint64_t count, int32_t width, int32_t height, UmaDecodedImage* image)
{
    if (!image) return UMA_ERROR_INVALID_ARGUMENT;
    *image = nullptr;
    UmaAssistant::Vision::GrayImage pixels;
    auto const result = UmaAssistant::CoreApi::compose_gray_images(sources, placements, count, width, height, pixels);
    return result == UMA_SUCCESS ? publish_decoded_image(std::move(pixels), image) : result;
}
int32_t UMA_API UmaCountInkGroups(UmaImageView const* source, int32_t threshold, int32_t* groups)
{
    return UmaAssistant::CoreApi::count_ink_groups(source, threshold, groups);
}
int32_t UMA_API UmaSampleImageDifference(UmaImageView const* before, UmaImageView const* after,
    UmaImagePoint const* points, uint64_t count, uint64_t* difference)
{
    return UmaAssistant::CoreApi::sample_image_difference(before, after, points, count, difference);
}

int32_t UMA_API UmaBinarizeColor(UmaImageView const* source, UmaColorRule const* rule, UmaDecodedImage* image)
{
    if (image == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    *image = nullptr;
    try
    {
        UmaAssistant::Vision::GrayImage pixels;
        auto const result = UmaAssistant::CoreApi::binarize_color(source, rule, pixels);
        return result == UMA_SUCCESS ? publish_decoded_image(std::move(pixels), image) : result;
    }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}
int32_t UMA_API UmaFindColorBlobs(UmaImageView const* source, UmaColorRule const* rule, UmaImageRect const* region,
    UmaColorBlob* destination, uint64_t capacity, uint64_t* required_count)
{
    return UmaAssistant::CoreApi::find_color_blobs(source, rule, region, destination, capacity, required_count);
}
int32_t UMA_API UmaCountColor(UmaImageView const* source, UmaColorRule const* rule, UmaImageRect const* region,
    UmaImageRect const* exclude, uint64_t* matching, uint64_t* sampled)
{
    return UmaAssistant::CoreApi::count_color(source, rule, region, exclude, matching, sampled);
}

int32_t UMA_API UmaMatchTemplate(
    UmaHandle handle, UmaImageView const* screen, UmaImageView const* template_image,
    UmaTemplateMatchOptions const* options, UmaTemplateMatchResult* result)
{
    return UmaAssistant::CoreApi::match_template(handle, screen, template_image, options, result);
}

int32_t UMA_API UmaRecognizeText(
    UmaHandle handle, UmaImageView const* image, char const* utf8_language,
    char* json_destination, uint64_t capacity, uint64_t* required_length)
{
    return UmaAssistant::CoreApi::recognize_text(
        handle, image, utf8_language, json_destination, capacity, required_length);
}

int32_t UMA_API UmaCompareImageChange(
    UmaHandle handle, UmaImageView const* before, UmaImageView const* after,
    UmaTemplateMatchResult const* match, int32_t* changed)
{
    return UmaAssistant::CoreApi::compare_image_change(handle, before, after, match, changed);
}

int32_t UMA_API UmaComparePersistentImageChange(
    UmaHandle handle, UmaImageView const* before, UmaImageView const* first_after,
    UmaImageView const* settled_after, UmaTemplateMatchResult const* match, int32_t* changed)
{
    return UmaAssistant::CoreApi::compare_persistent_image_change(
        handle, before, first_after, settled_after, match, changed);
}

int32_t UMA_API UmaDecodeImageFile(char const* utf8_path, UmaDecodedImage* decoded_image)
{
    if (decoded_image == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    *decoded_image = nullptr;
    if (utf8_path == nullptr || *utf8_path == '\0') return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        return publish_decoded_image(
            UmaAssistant::Resources::WicImageDecoder::decode_file_utf8(utf8_path), decoded_image);
    }
    catch (...)
    {
        return UMA_ERROR_RECOGNITION_FAILED;
    }
}

int32_t UMA_API UmaDecodeImageBytes(
    uint8_t const* encoded_bytes, uint64_t encoded_length, UmaDecodedImage* decoded_image)
{
    if (decoded_image == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    *decoded_image = nullptr;
    if (encoded_bytes == nullptr || encoded_length == 0
        || encoded_length > (std::numeric_limits<std::size_t>::max)()
        || encoded_length > (std::numeric_limits<std::uint32_t>::max)())
        return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        auto const input = std::span<std::uint8_t const>(
            encoded_bytes, static_cast<std::size_t>(encoded_length));
        return publish_decoded_image(
            UmaAssistant::Resources::WicImageDecoder::decode_bytes(input), decoded_image);
    }
    catch (...)
    {
        return UMA_ERROR_RECOGNITION_FAILED;
    }
}

int32_t UMA_API UmaCreateImageFromRgba(
    int32_t width, int32_t height, uint8_t const* rgba_bytes, uint64_t rgba_length,
    UmaDecodedImage* decoded_image)
{
    if (decoded_image == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    *decoded_image = nullptr;
    if (width <= 0 || height <= 0 || rgba_bytes == nullptr || rgba_length == 0
        || rgba_length > (std::numeric_limits<std::size_t>::max)())
        return UMA_ERROR_INVALID_ARGUMENT;
    try
    {
        auto const rgba = std::span<std::uint8_t const>(
            rgba_bytes, static_cast<std::size_t>(rgba_length));
        return publish_decoded_image(
            UmaAssistant::Resources::WicImageDecoder::from_rgba(width, height, rgba), decoded_image);
    }
    catch (std::invalid_argument const&)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
    catch (...)
    {
        return UMA_ERROR_RECOGNITION_FAILED;
    }
}

int32_t UMA_API UmaGetDecodedImageInfo(
    UmaDecodedImage decoded_image, UmaDecodedImageInfo* info)
{
    if (decoded_image == nullptr || info == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    *info = {};
    auto const& image = decoded_image->value->image;
    if (image.width <= 0 || image.height <= 0
        || image.gray.size() != static_cast<std::size_t>(image.width) * image.height
        || (!image.rgba.empty() && image.rgba.size() != image.gray.size() * 4u))
        return UMA_ERROR_RECOGNITION_FAILED;
    info->width = image.width;
    info->height = image.height;
    info->gray_length = image.gray.size();
    info->rgba_length = image.rgba.size();
    info->preparation = static_cast<std::int32_t>(decoded_image->value->preparation);
    return UMA_SUCCESS;
}

int32_t UMA_API UmaCopyDecodedImage(
    UmaDecodedImage decoded_image,
    uint8_t* gray_destination,
    uint64_t gray_capacity,
    uint8_t* rgba_destination,
    uint64_t rgba_capacity)
{
    if (decoded_image == nullptr) return UMA_ERROR_INVALID_ARGUMENT;
    auto const& image = decoded_image->value->image;
    auto const gray_size = static_cast<uint64_t>(image.gray.size());
    auto const rgba_size = static_cast<uint64_t>(image.rgba.size());
    if (gray_capacity < gray_size || rgba_capacity < rgba_size)
        return UMA_ERROR_BUFFER_TOO_SMALL;
    if (gray_destination == nullptr || (rgba_size != 0 && rgba_destination == nullptr))
        return UMA_ERROR_INVALID_ARGUMENT;
    std::memcpy(gray_destination, image.gray.data(), image.gray.size());
    if (!image.rgba.empty()) std::memcpy(rgba_destination, image.rgba.data(), image.rgba.size());
    return UMA_SUCCESS;
}

void UMA_API UmaReleaseDecodedImage(UmaDecodedImage decoded_image)
{
    delete decoded_image;
}

int32_t UMA_API UmaLoadTemplateImage(UmaHandle handle, char const* utf8_path,
    int32_t preparation, UmaDecodedImage* decoded_image)
{
    if (!decoded_image) return UMA_ERROR_INVALID_ARGUMENT;
    *decoded_image = nullptr;
    try
    {
        std::shared_ptr<UmaAssistant::Resources::PreparedTemplate const> image;
        auto const error = UmaAssistant::CoreApi::load_template_image(handle, utf8_path, preparation, image);
        if (error != UMA_SUCCESS) return error;
        *decoded_image = new UmaDecodedImageImpl{std::move(image)};
        return UMA_SUCCESS;
    }
    catch (...) { return UMA_ERROR_RECOGNITION_FAILED; }
}

int32_t UMA_API UmaRefreshResourceSnapshot(UmaHandle handle)
{
    return UmaAssistant::CoreApi::refresh_resource_snapshot(handle);
}

int32_t UMA_API UmaTransformImage(UmaImageView const* source,
    UmaImageTransformOptions const* options, UmaDecodedImage* decoded_image)
{
    if (!decoded_image) return UMA_ERROR_INVALID_ARGUMENT;
    *decoded_image = nullptr;
    UmaAssistant::Vision::GrayImage image;
    auto const error = UmaAssistant::CoreApi::transform_image(source, options, image);
    if (error != UMA_SUCCESS) return error;
    return publish_decoded_image(std::move(image), decoded_image);
}


int32_t UMA_API UmaValidatePipelineJson(
    char const* utf8_json,
    char* error_destination,
    uint64_t capacity,
    uint64_t* required_length)
{
    return UmaAssistant::CoreApi::validate_pipeline_json(
        utf8_json, error_destination, capacity, required_length);
}

UmaStartResult UMA_API UmaRunPipelineAsync(
    UmaHandle handle,
    char const* utf8_pipeline_json,
    char const* utf8_entry_task)
{
    if (utf8_pipeline_json == nullptr) return {0, UMA_ERROR_INVALID_ARGUMENT};
    return UmaAssistant::CoreApi::start_run_pipeline(
        handle,
        utf8_pipeline_json,
        utf8_entry_task ? utf8_entry_task : "");
}

UmaStartResult UMA_API UmaRunGameTaskAsync(
    UmaHandle handle,
    char const* utf8_task_id,
    char const* utf8_settings_json)
{
    if (handle == nullptr || utf8_task_id == nullptr || *utf8_task_id == '\0'
        || utf8_settings_json == nullptr || *utf8_settings_json == '\0')
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    try
    {
        return UmaAssistant::CoreApi::start_run_game_task(
            handle, std::string{utf8_task_id}, std::string{utf8_settings_json});
    }
    catch (...)
    {
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    }
}

UmaAppendTaskResult UMA_API UmaAppendTask(
    UmaHandle handle,
    const char* utf8_task_id,
    const char* utf8_settings_json)
{
    try
    {
        return UmaAssistant::CoreApi::append_task(handle, utf8_task_id, utf8_settings_json);
    }
    catch (...)
    {
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    }
}

int32_t UMA_API UmaSetTaskParams(
    UmaHandle handle,
    uint64_t task_id,
    const char* utf8_settings_json)
{
    try
    {
        return UmaAssistant::CoreApi::set_task_params(handle, task_id, utf8_settings_json);
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

int32_t UMA_API UmaClearTasks(UmaHandle handle)
{
    try
    {
        return UmaAssistant::CoreApi::clear_tasks(handle);
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

UmaStartResult UMA_API UmaStartAsync(UmaHandle handle)
{
    try
    {
        return UmaAssistant::CoreApi::start_queue(handle);
    }
    catch (...)
    {
        return {0, UMA_ERROR_INVALID_ARGUMENT};
    }
}

int32_t UMA_API UmaGetRunSnapshotJson(
    UmaHandle handle,
    char* destination,
    uint64_t capacity,
    uint64_t* required_length)
{
    try
    {
        return UmaAssistant::CoreApi::get_run_snapshot_json(
            handle, destination, capacity, required_length);
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

int32_t UMA_API UmaGetCatalogJson(
    UmaHandle handle,
    const char* utf8_catalog_type,
    char* destination,
    uint64_t capacity,
    uint64_t* required_length)
{
    try
    {
        return UmaAssistant::CoreApi::get_catalog_json(
            handle, utf8_catalog_type, destination, capacity, required_length);
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

}
