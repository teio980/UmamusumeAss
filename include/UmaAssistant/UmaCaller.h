#pragma once

#include <stdint.h>

#if defined(_WIN32)
#  define UMA_CALL __stdcall
#  if defined(UMA_DLL_EXPORTS)
#    define UMA_API_PORT __declspec(dllexport)
#  else
#    define UMA_API_PORT __declspec(dllimport)
#  endif
#else
#  define UMA_CALL
#  if defined(UMA_DLL_EXPORTS)
#    define UMA_API_PORT __attribute__((visibility("default")))
#  else
#    define UMA_API_PORT
#  endif
#endif

#define UMA_API UMA_API_PORT UMA_CALL
#define UMA_API_VERSION 11u

typedef struct UmaHandleImpl* UmaHandle;
typedef struct UmaDecodedImageImpl* UmaDecodedImage;

typedef struct UmaAppendTaskResult
{
    uint64_t task_id;
    int32_t error_code;
} UmaAppendTaskResult;

typedef struct UmaDecodedImageInfo
{
    int32_t width;
    int32_t height;
    uint64_t gray_length;
    uint64_t rgba_length;
    int32_t preparation;
    int32_t reserved;
} UmaDecodedImageInfo;

typedef struct UmaImageTransformOptions
{
    int32_t crop_x;
    int32_t crop_y;
    int32_t crop_width;
    int32_t crop_height;
    int32_t target_width;
    int32_t target_height;
    int32_t mean_gray;
    int32_t nearest;
} UmaImageTransformOptions;

#define UMA_TEMPLATE_ORIGINAL 0
#define UMA_TEMPLATE_MASKED_HEAD 1
#define UMA_TEMPLATE_PORTRAIT 2

typedef struct UmaColorRule
{
    int32_t minimum_red, minimum_green, minimum_blue;
    int32_t maximum_red, maximum_green, maximum_blue;
    int32_t minimum_red_green, maximum_red_green;
    int32_t minimum_green_blue, maximum_green_blue;
    int32_t use_hsv, sample_step;
    double hue_min, hue_max, saturation_min, saturation_max, value_min, value_max;
} UmaColorRule;
typedef struct UmaImageRect { int32_t x, y, width, height; } UmaImageRect;
typedef struct UmaColorBlob { int32_t area, x, y, width, height; } UmaColorBlob;

typedef struct UmaStartResult
{
    uint64_t operation_id;
    int32_t error_code;
} UmaStartResult;

typedef struct UmaImageView
{
    int32_t width;
    int32_t height;
    const uint8_t* gray_pixels;
    uint64_t gray_length;
    const uint8_t* rgba_pixels;
    uint64_t rgba_length;
} UmaImageView;

typedef struct UmaTemplateMatchOptions
{
    uint32_t method;
    int32_t has_roi;
    int32_t roi_x;
    int32_t roi_y;
    int32_t roi_width;
    int32_t roi_height;
    double threshold;
    int32_t reference_width;
    int32_t reference_height;
    int32_t candidate_step_override;
    int32_t candidate_step;
    int32_t sample_width;
    int32_t sample_height;
    int32_t full_search_on_miss;
    int32_t require_text_contrast;
    const double* scales;
    uint64_t scale_count;
} UmaTemplateMatchOptions;

typedef struct UmaTemplateMatchResult
{
    int32_t found;
    double score;
    int32_t x;
    int32_t y;
    int32_t width;
    int32_t height;
} UmaTemplateMatchResult;

typedef void (UMA_CALL* UmaApiCallback)(
    int32_t message,
    const char* details_json,
    void* custom_arg);

#define UMA_MSG_CONNECTION_STARTED 1
#define UMA_MSG_CONNECTION_PROGRESS 2
#define UMA_MSG_CONNECTION_SUCCEEDED 3
#define UMA_MSG_CONNECTION_FAILED 4
#define UMA_MSG_GAME_VERIFIED 5
#define UMA_MSG_GAME_VERIFICATION_FAILED 6
#define UMA_MSG_FRAME_CAPTURED 7
#define UMA_MSG_INPUT_SUCCEEDED 8
#define UMA_MSG_INPUT_FAILED 9
#define UMA_MSG_DEVICE_DISCONNECTED 10
#define UMA_MSG_OPERATION_FAILED 11
#define UMA_MSG_DEVICE_RECONNECTED 12
#define UMA_MSG_PACKAGE_OPERATION_COMPLETED 13
#define UMA_MSG_TASK_PROGRESS 14
#define UMA_MSG_PIPELINE_COMPLETED 15
#define UMA_MSG_PIPELINE_FAILED 16
#define UMA_MSG_GAME_TASK_PROGRESS 17
#define UMA_MSG_GAME_TASK_COMPLETED 18
#define UMA_MSG_GAME_TASK_FAILED 19
#define UMA_MSG_RUNTIME_DIAGNOSTIC 20
#define UMA_MSG_TASK_STARTED 21
#define UMA_MSG_TASK_DECISION 22
#define UMA_MSG_TASK_ACTION_CONFIRMED 23
#define UMA_MSG_TASK_COMPLETED 24
#define UMA_MSG_TASK_FAILED 25
#define UMA_MSG_QUEUE_COMPLETED 26
#define UMA_MSG_QUEUE_FAILED 27

#define UMA_SUCCESS 0
#define UMA_ERROR_ADB_EXECUTABLE_NOT_FOUND 1
#define UMA_ERROR_PROCESS_START_FAILED 2
#define UMA_ERROR_COMMAND_TIMED_OUT 3
#define UMA_ERROR_DEVICE_UNAUTHORIZED 4
#define UMA_ERROR_DEVICE_OFFLINE 5
#define UMA_ERROR_DEVICE_UNAVAILABLE 6
#define UMA_ERROR_COMMAND_FAILED 7
#define UMA_ERROR_INVALID_DEVICE_RESPONSE 8
#define UMA_ERROR_CANCELED 9
#define UMA_ERROR_DEVICE_NOT_READY 10
#define UMA_ERROR_INVALID_ARGUMENT 11
#define UMA_ERROR_BUSY 12
#define UMA_ERROR_BOOT_NOT_COMPLETED 13
#define UMA_ERROR_TARGET_GAME_NOT_INSTALLED 14
#define UMA_ERROR_DEVICE_DISCONNECTED 15
#define UMA_ERROR_BUFFER_TOO_SMALL 16
#define UMA_ERROR_RECOGNITION_FAILED 17

#define UMA_MATCH_TEMPLATE_GRAY 0u
#define UMA_MATCH_TEMPLATE_COLOR 1u
#define UMA_MATCH_TEMPLATE_BUTTON 2u
#define UMA_MATCH_TEMPLATE_SCALED 3u
#define UMA_MATCH_TEMPLATE_SCALED_MASKED 4u
// These methods take an ROI in source pixels. For SCREENSHOT_CROP, the
// reference dimensions specify the candidate width and height in pixels.
#define UMA_MATCH_TEMPLATE_MASKED_REGION 5u
#define UMA_MATCH_TEMPLATE_SCREENSHOT_CROP 6u
// Fixed source-pixel region, sampled only at opaque colored template pixels.
#define UMA_MATCH_TEMPLATE_COLORED_REGION 7u
#define UMA_MATCH_TEMPLATE_EXHAUSTIVE_COLOR 8u

#define UMA_TOUCH_DOWN 0
#define UMA_TOUCH_UP 1
#define UMA_TOUCH_CANCEL 2

#define UMA_PACKAGE_START 0
#define UMA_PACKAGE_START_ACTIVITY 1
#define UMA_PACKAGE_STOP 2
#define UMA_PACKAGE_QUERY_RUNNING 3

#ifdef __cplusplus
extern "C" {
#endif

typedef struct UmaImagePoint { int32_t x; int32_t y; } UmaImagePoint;
// A successful glyph preparation may return null for ambiguous glyphs.
int32_t UMA_API UmaPrepareNumericGlyphs(const UmaImageView* source, int32_t warm_gain,
    const UmaColorRule* seed_rule, UmaDecodedImage* image);
int32_t UMA_API UmaComposeGrayImages(const UmaImageView* sources, const UmaImageRect* placements,
    uint64_t count, int32_t width, int32_t height, UmaDecodedImage* image);
int32_t UMA_API UmaCountInkGroups(const UmaImageView* source, int32_t threshold, int32_t* groups);
int32_t UMA_API UmaSampleImageDifference(const UmaImageView* before, const UmaImageView* after,
    const UmaImagePoint* points, uint64_t count, uint64_t* difference);
UMA_API_PORT const char* UMA_CALL UmaGetVersion(void);
int32_t UMA_API UmaBinarizeColor(const UmaImageView* source, const UmaColorRule* rule, UmaDecodedImage* image);
int32_t UMA_API UmaFindColorBlobs(const UmaImageView* source, const UmaColorRule* rule, const UmaImageRect* region,
    UmaColorBlob* destination, uint64_t capacity, uint64_t* required_count);
int32_t UMA_API UmaCountColor(const UmaImageView* source, const UmaColorRule* rule, const UmaImageRect* region,
    const UmaImageRect* exclude, uint64_t* matching, uint64_t* sampled);
uint32_t UMA_API UmaGetApiVersion(void);
UmaHandle UMA_API UmaCreate(UmaApiCallback callback, void* custom_arg);
void UMA_API UmaDestroy(UmaHandle handle);
int32_t UMA_API UmaSetUserDir(const char* utf8_path);
int32_t UMA_API UmaLoadResource(const char* utf8_path);
int32_t UMA_API UmaSetInstanceOptions(UmaHandle handle, const char* utf8_json);
UmaStartResult UMA_API UmaConnectAsync(UmaHandle handle,
                                       const char* adb_path,
                                       const char* serial,
                                       const char* profile);
int32_t UMA_API UmaCancelConnect(UmaHandle handle, uint64_t operation_id);
int32_t UMA_API UmaCancelOperation(UmaHandle handle, uint64_t operation_id);

UmaStartResult UMA_API UmaVerifyGameAsync(UmaHandle handle, const char* utf8_package_id);
UmaStartResult UMA_API UmaCaptureAsync(UmaHandle handle);
int32_t UMA_API UmaGetFramePngSize(UmaHandle handle, uint64_t frame_id, uint64_t* size);
int32_t UMA_API UmaCopyFramePng(UmaHandle handle, uint64_t frame_id,
                                uint8_t* destination, uint64_t capacity);
int32_t UMA_API UmaReleaseFrame(UmaHandle handle, uint64_t frame_id);
UmaStartResult UMA_API UmaTapAsync(UmaHandle handle, uint64_t frame_id,
                                   int32_t canonical_x, int32_t canonical_y);
UmaStartResult UMA_API UmaSwipeAsync(UmaHandle handle, uint64_t frame_id,
                                     int32_t x1, int32_t y1, int32_t x2, int32_t y2,
                                     int32_t duration_ms);
UmaStartResult UMA_API UmaSwipeWithOptionsAsync(UmaHandle handle, uint64_t frame_id,
                                                int32_t x1, int32_t y1,
                                                int32_t x2, int32_t y2,
                                                int32_t duration_ms,
                                                int32_t extra_swipe);
UmaStartResult UMA_API UmaTouchAsync(UmaHandle handle, uint64_t frame_id,
                                     int32_t canonical_x, int32_t canonical_y,
                                     int32_t phase);
UmaStartResult UMA_API UmaPrepareTouchAsync(UmaHandle handle, uint64_t frame_id);
UmaStartResult UMA_API UmaKeyEventAsync(UmaHandle handle, uint64_t frame_id,
                                        const char* utf8_key_code);
UmaStartResult UMA_API UmaInputTextAsync(UmaHandle handle, uint64_t frame_id,
                                         const char* utf8_text);
UmaStartResult UMA_API UmaPackageOperationAsync(UmaHandle handle,
                                                const char* utf8_package_id,
                                                const char* utf8_activity,
                                                int32_t operation);
int32_t UMA_API UmaMatchTemplate(UmaHandle handle,
                                 const UmaImageView* screen,
                                 const UmaImageView* template_image,
                                 const UmaTemplateMatchOptions* options,
                                 UmaTemplateMatchResult* result);
int32_t UMA_API UmaRecognizeText(UmaHandle handle,
                                 const UmaImageView* image,
                                 const char* utf8_language,
                                 char* json_destination,
                                 uint64_t capacity,
                                 uint64_t* required_length);
int32_t UMA_API UmaCompareImageChange(UmaHandle handle,
                                      const UmaImageView* before,
                                      const UmaImageView* after,
                                      const UmaTemplateMatchResult* match,
                                      int32_t* changed);
int32_t UMA_API UmaComparePersistentImageChange(UmaHandle handle,
                                               const UmaImageView* before,
                                               const UmaImageView* first_after,
                                               const UmaImageView* settled_after,
                                               const UmaTemplateMatchResult* match,
                                               int32_t* changed);

// Decoded images are opaque, immutable native values. Callers copy both planes
// into caller-owned buffers and release the value on every success or failure
// path. Encoded input is decoded from its first WIC frame as straight RGBA8.
int32_t UMA_API UmaDecodeImageFile(const char* utf8_path, UmaDecodedImage* decoded_image);
int32_t UMA_API UmaDecodeImageBytes(const uint8_t* encoded_bytes,
                                    uint64_t encoded_length,
                                    UmaDecodedImage* decoded_image);
int32_t UMA_API UmaCreateImageFromRgba(int32_t width,
                                       int32_t height,
                                       const uint8_t* rgba_bytes,
                                       uint64_t rgba_length,
                                       UmaDecodedImage* decoded_image);
int32_t UMA_API UmaGetDecodedImageInfo(UmaDecodedImage decoded_image,
                                       UmaDecodedImageInfo* info);
int32_t UMA_API UmaCopyDecodedImage(UmaDecodedImage decoded_image,
                                    uint8_t* gray_destination,
                                    uint64_t gray_capacity,
                                    uint8_t* rgba_destination,
                                    uint64_t rgba_capacity);
void UMA_API UmaReleaseDecodedImage(UmaDecodedImage decoded_image);
// Templates are cached by the handle's pinned resource snapshot. Refresh is
// permitted only while device operations and recognition are idle.
int32_t UMA_API UmaLoadTemplateImage(UmaHandle handle, const char* utf8_path,
    int32_t preparation, UmaDecodedImage* decoded_image);
int32_t UMA_API UmaRefreshResourceSnapshot(UmaHandle handle);
int32_t UMA_API UmaTransformImage(const UmaImageView* source,
    const UmaImageTransformOptions* options, UmaDecodedImage* decoded_image);

int32_t UMA_API UmaValidatePipelineJson(
    const char* utf8_json,
    char* error_destination,
    uint64_t capacity,
    uint64_t* required_length);
UmaStartResult UMA_API UmaRunPipelineAsync(
    UmaHandle handle,
    const char* utf8_pipeline_json,
    const char* utf8_entry_task);
UmaStartResult UMA_API UmaRunGameTaskAsync(
    UmaHandle handle,
    const char* utf8_task_id,
    const char* utf8_settings_json);

UmaAppendTaskResult UMA_API UmaAppendTask(
    UmaHandle handle,
    const char* utf8_task_id,
    const char* utf8_settings_json);
int32_t UMA_API UmaSetTaskParams(
    UmaHandle handle,
    uint64_t task_id,
    const char* utf8_settings_json);
int32_t UMA_API UmaClearTasks(UmaHandle handle);
UmaStartResult UMA_API UmaStartAsync(UmaHandle handle);
int32_t UMA_API UmaGetRunSnapshotJson(
    UmaHandle handle,
    char* destination,
    uint64_t capacity,
    uint64_t* required_length);
int32_t UMA_API UmaGetCatalogJson(
    UmaHandle handle,
    const char* utf8_catalog_type,
    char* destination,
    uint64_t capacity,
    uint64_t* required_length);

#ifdef __cplusplus
}
#endif
