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
#  define UMA_API_PORT
#endif

#define UMA_API UMA_API_PORT UMA_CALL

typedef struct UmaHandleImpl* UmaHandle;

typedef struct UmaStartResult
{
    uint64_t operation_id;
    int32_t error_code;
} UmaStartResult;

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

#ifdef __cplusplus
extern "C" {
#endif

UMA_API_PORT const char* UMA_CALL UmaGetVersion(void);
UmaHandle UMA_API UmaCreate(UmaApiCallback callback, void* custom_arg);
void UMA_API UmaDestroy(UmaHandle handle);
int32_t UMA_API UmaSetUserDir(const char* utf8_path);
int32_t UMA_API UmaLoadResource(const char* utf8_path);
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

#ifdef __cplusplus
}
#endif
