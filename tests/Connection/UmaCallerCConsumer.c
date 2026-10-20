







#include "UmaAssistant/UmaCaller.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if UMA_MSG_RUNTIME_DIAGNOSTIC != 20
#error "Runtime diagnostic callback ID must remain 20"
#endif

#if UMA_MSG_QUEUE_FAILED != 27
#error "Queue failed callback ID must be 27"
#endif

typedef char decoded_image_info_layout[(sizeof(UmaDecodedImageInfo) == 32) ? 1 : -1];
typedef char image_transform_options_layout[(sizeof(UmaImageTransformOptions) == 32) ? 1 : -1];
typedef char color_rule_layout[(sizeof(UmaColorRule) == 96) ? 1 : -1];

static FILE* open_fixture_file(char const* path)
{
#if defined(_MSC_VER)
    FILE* file = NULL;
    return fopen_s(&file, path, "rb") == 0 ? file : NULL;
#else
    return fopen(path, "rb");
#endif
}





struct consumer_context
{
    int32_t     first_message_id;
    char        first_json[256];
    int         terminal_seen;
};

static void UMA_CALL consumer_callback(
    int32_t       message,
    char const*   details_json,
    void*         custom_arg)
{
    struct consumer_context* ctx =
        (struct consumer_context*)custom_arg;

    if (ctx->first_message_id == 0 && details_json)
    {
        ctx->first_message_id = message;
#if defined(_WIN32)
        strncpy_s(ctx->first_json, sizeof(ctx->first_json),
                  details_json, _TRUNCATE);
#else
        snprintf(ctx->first_json, sizeof(ctx->first_json), "%s", details_json);
#endif
    }

    if (message == UMA_MSG_CONNECTION_SUCCEEDED
        || message == UMA_MSG_CONNECTION_FAILED)
    {
        ctx->terminal_seen = 1;
    }

    (void)0;
}





static int run_image_decode_smoke_test(void)
{
    int failures = 0;
    char path[1024];
    FILE* input = NULL;
    long file_length = 0;
    uint8_t* encoded = NULL;
    uint8_t* file_gray = NULL;
    uint8_t* file_rgba = NULL;
    uint8_t* bytes_gray = NULL;
    uint8_t* bytes_rgba = NULL;
    UmaDecodedImage file_image = NULL;
    UmaDecodedImage bytes_image = NULL;
    UmaDecodedImage raw_image = NULL;
    UmaDecodedImageInfo file_info = {0};
    UmaDecodedImageInfo bytes_info = {0};
    UmaDecodedImageInfo raw_info = {0};
    uint8_t raw_source[4] = {255, 0, 0, 128};

    if (UmaDecodeImageFile(NULL, &file_image) != UMA_ERROR_INVALID_ARGUMENT
        || file_image != NULL)
    {
        printf("FAIL: UmaDecodeImageFile validates a null UTF-8 path\n");
        ++failures;
    }
    if (UmaDecodeImageBytes(NULL, 0, &bytes_image) != UMA_ERROR_INVALID_ARGUMENT
        || bytes_image != NULL)
    {
        printf("FAIL: UmaDecodeImageBytes validates empty input\n");
        ++failures;
    }
    if (snprintf(path, sizeof(path), "%s/resource/uma/assets/images/global/trainees/100601.webp",
                 CMAKE_SOURCE_DIR) < 0)
    {
        printf("FAIL: could not build image decode fixture path\n");
        ++failures;
        goto cleanup;
    }
    input = open_fixture_file(path);
    if (input == NULL || fseek(input, 0, SEEK_END) != 0
        || (file_length = ftell(input)) <= 0 || fseek(input, 0, SEEK_SET) != 0)
    {
        printf("FAIL: could not read image decode fixture\n");
        ++failures;
        goto cleanup;
    }
    encoded = (uint8_t*)malloc((size_t)file_length);
    if (encoded == NULL || fread(encoded, 1, (size_t)file_length, input) != (size_t)file_length)
    {
        printf("FAIL: could not load image decode fixture bytes\n");
        ++failures;
        goto cleanup;
    }
    if (UmaDecodeImageFile(path, &file_image) != UMA_SUCCESS || file_image == NULL
        || UmaDecodeImageBytes(encoded, (uint64_t)file_length, &bytes_image) != UMA_SUCCESS
        || bytes_image == NULL
        || UmaGetDecodedImageInfo(file_image, &file_info) != UMA_SUCCESS
        || UmaGetDecodedImageInfo(bytes_image, &bytes_info) != UMA_SUCCESS
        || file_info.width != bytes_info.width || file_info.height != bytes_info.height
        || file_info.gray_length != bytes_info.gray_length
        || file_info.rgba_length != bytes_info.rgba_length)
    {
        printf("FAIL: native WIC file and byte decoding returned inconsistent metadata\n");
        ++failures;
        goto cleanup;
    }
    file_gray = (uint8_t*)malloc((size_t)file_info.gray_length);
    file_rgba = (uint8_t*)malloc((size_t)file_info.rgba_length);
    bytes_gray = (uint8_t*)malloc((size_t)bytes_info.gray_length);
    bytes_rgba = (uint8_t*)malloc((size_t)bytes_info.rgba_length);
    if (file_gray == NULL || file_rgba == NULL || bytes_gray == NULL || bytes_rgba == NULL
        || UmaCopyDecodedImage(file_image, file_gray, file_info.gray_length,
                               file_rgba, file_info.rgba_length) != UMA_SUCCESS
        || UmaCopyDecodedImage(bytes_image, bytes_gray, bytes_info.gray_length,
                               bytes_rgba, bytes_info.rgba_length) != UMA_SUCCESS
        || memcmp(file_gray, bytes_gray, (size_t)file_info.gray_length) != 0
        || memcmp(file_rgba, bytes_rgba, (size_t)file_info.rgba_length) != 0)
    {
        printf("FAIL: WIC file and byte decoding returned different pixel planes\n");
        ++failures;
        goto cleanup;
    }
    if (UmaCreateImageFromRgba(1, 1, raw_source, sizeof(raw_source), &raw_image) != UMA_SUCCESS
        || raw_image == NULL
        || UmaGetDecodedImageInfo(raw_image, &raw_info) != UMA_SUCCESS
        || raw_info.gray_length != 1 || raw_info.rgba_length != 4)
    {
        printf("FAIL: UmaCreateImageFromRgba returned invalid pixel plane sizes\n");
        ++failures;
        goto cleanup;
    }
    {
        uint8_t gray = 0;
        uint8_t rgba[4] = {0};
        if (UmaCopyDecodedImage(raw_image, &gray, 1, rgba, sizeof(rgba)) != UMA_SUCCESS
            || gray != 76 || memcmp(rgba, raw_source, sizeof(raw_source)) != 0)
        {
            printf("FAIL: raw RGBA decode changed bytes or legacy grayscale value\n");
            ++failures;
            goto cleanup;
        }
    }
    printf("PASS: C ABI decodes WIC file/bytes and preserves raw RGBA and grayscale\n");

cleanup:
    if (input != NULL) fclose(input);
    free(encoded);
    free(file_gray);
    free(file_rgba);
    free(bytes_gray);
    free(bytes_rgba);
    UmaReleaseDecodedImage(file_image);
    UmaReleaseDecodedImage(bytes_image);
    UmaReleaseDecodedImage(raw_image);
    return failures;
}

static int run_image_transform_smoke_test(void)
{
    uint8_t const rgba[4] = {255, 0, 0, 128};
    UmaImageView source = {1, 1, NULL, 0, rgba, sizeof(rgba)};
    UmaImageTransformOptions options = {0};
    UmaDecodedImage decoded = NULL;
    UmaDecodedImageInfo info = {0};
    uint8_t gray[4] = {0};
    uint8_t pixels[16] = {0};
    options.target_width = 2;
    options.target_height = 2;
    options.nearest = 1;
    if (UmaTransformImage(&source, &options, &decoded) != UMA_SUCCESS || decoded == NULL)
        return 1;
    if (UmaGetDecodedImageInfo(decoded, &info) != UMA_SUCCESS || info.width != 2 || info.height != 2
        || info.gray_length != sizeof(gray) || info.rgba_length != sizeof(pixels)
        || UmaCopyDecodedImage(decoded, gray, sizeof(gray), pixels, sizeof(pixels)) != UMA_SUCCESS
        || gray[0] != 76 || gray[3] != 76 || memcmp(pixels + 12, rgba, sizeof(rgba)) != 0)
    {
        UmaReleaseDecodedImage(decoded);
        printf("FAIL: C ABI nearest resize changed pixel values or struct layout\n");
        return 1;
    }
    UmaReleaseDecodedImage(decoded);
    printf("PASS: C ABI native image transforms preserve pixel planes\n");
    return 0;
}

int run_abi_smoke_test(void)
{
    int failures = 0;


    char const* ver = UmaGetVersion();
    if (!ver || strlen(ver) == 0)
    {
        printf("FAIL: UmaGetVersion returned empty\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaGetVersion = %s\n", ver);
    }

    uint32_t api_version = UmaGetApiVersion();
    if (api_version != UMA_API_VERSION)
    {
        printf("FAIL: UmaGetApiVersion = %u, expected %u\n",
               (unsigned int)api_version, (unsigned int)UMA_API_VERSION);
        ++failures;
    }
    else
    {
        printf("PASS: UmaGetApiVersion = %u\n", (unsigned int)api_version);
    }

    failures += run_image_decode_smoke_test();
    failures += run_image_transform_smoke_test();

    int32_t options_result = UmaSetInstanceOptions(NULL, "{\"version\":1}");
    if (options_result != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaSetInstanceOptions(NULL) returned %d\n", (int)options_result);
        ++failures;
    }
    else
    {
        printf("PASS: UmaSetInstanceOptions rejects a null handle\n");
    }

    UmaStartResult prepare_touch_result = UmaPrepareTouchAsync(NULL, 1);
    if (prepare_touch_result.error_code != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaPrepareTouchAsync rejects a null handle, got %d\n",
               (int)prepare_touch_result.error_code);
        ++failures;
    }
    else
    {
        printf("PASS: UmaPrepareTouchAsync rejects a null handle\n");
    }

    UmaStartResult key_event_result = UmaKeyEventAsync(NULL, 1, "KEYCODE_BACK");
    if (key_event_result.error_code != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaKeyEventAsync rejects a null handle, got %d\n",
               (int)key_event_result.error_code);
        ++failures;
    }
    else
    {
        printf("PASS: UmaKeyEventAsync rejects a null handle\n");
    }

    UmaStartResult input_text_result = UmaInputTextAsync(NULL, 1, "test");
    if (input_text_result.error_code != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaInputTextAsync rejects a null handle, got %d\n",
               (int)input_text_result.error_code);
        ++failures;
    }
    else
    {
        printf("PASS: UmaInputTextAsync rejects a null handle\n");
    }

    UmaStartResult package_operation_result = UmaPackageOperationAsync(
        NULL, "com.example.game", NULL, UMA_PACKAGE_START);
    if (package_operation_result.error_code != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaPackageOperationAsync rejects a null handle, got %d\n",
               (int)package_operation_result.error_code);
        ++failures;
    }
    else
    {
        printf("PASS: UmaPackageOperationAsync rejects a null handle\n");
    }

    package_operation_result = UmaPackageOperationAsync(
        NULL, "com.example.game", "MainActivity", UMA_PACKAGE_START_ACTIVITY);
    if (package_operation_result.error_code != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaPackageOperationAsync rejects a null handle for activity start, got %d\n",
               (int)package_operation_result.error_code);
        ++failures;
    }
    else
    {
        printf("PASS: UmaPackageOperationAsync activity start validates the handle\n");
    }

    UmaStartResult game_task_result = UmaRunGameTaskAsync(NULL, "mail-collection", "{}");
    if (game_task_result.error_code != UMA_ERROR_INVALID_ARGUMENT || game_task_result.operation_id != 0)
    {
        printf("FAIL: UmaRunGameTaskAsync rejects a null handle without accepting an operation\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaRunGameTaskAsync rejects a null handle synchronously\n");
    }

    UmaAppendTaskResult append_res = UmaAppendTask(NULL, "mail-collection", "{}");
    if (append_res.error_code != UMA_ERROR_INVALID_ARGUMENT || append_res.task_id != 0)
    {
        printf("FAIL: UmaAppendTask rejects a null handle\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaAppendTask rejects a null handle synchronously\n");
    }

    if (UmaSetTaskParams(NULL, 1, "{}") != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaSetTaskParams rejects a null handle\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaSetTaskParams rejects a null handle\n");
    }

    if (UmaClearTasks(NULL) != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaClearTasks rejects a null handle\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaClearTasks rejects a null handle\n");
    }

    UmaStartResult queue_start_res = UmaStartAsync(NULL);
    if (queue_start_res.error_code != UMA_ERROR_INVALID_ARGUMENT || queue_start_res.operation_id != 0)
    {
        printf("FAIL: UmaStartAsync rejects a null handle\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaStartAsync rejects a null handle synchronously\n");
    }

    uint64_t snapshot_len = 0;
    if (UmaGetRunSnapshotJson(NULL, NULL, 0, &snapshot_len) != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaGetRunSnapshotJson rejects a null handle\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaGetRunSnapshotJson rejects a null handle\n");
    }

    uint64_t catalog_len = 0;
    if (UmaGetCatalogJson(NULL, "all", NULL, 0, &catalog_len) != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaGetCatalogJson rejects a null handle\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaGetCatalogJson rejects a null handle\n");
    }

    UmaTemplateMatchResult match_result = {0};
    if (UmaMatchTemplate(NULL, NULL, NULL, NULL, &match_result) != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaMatchTemplate rejects a null handle\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaMatchTemplate rejects a null handle\n");
    }

    uint64_t required_length = 0;
    if (UmaRecognizeText(NULL, NULL, NULL, NULL, 0, &required_length) != UMA_ERROR_INVALID_ARGUMENT)
    {
        printf("FAIL: UmaRecognizeText rejects a null handle\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaRecognizeText rejects a null handle\n");
    }


    struct consumer_context ctx = {0};
    UmaHandle h = UmaCreate(&consumer_callback, &ctx);
    if (h != NULL)
    {
        printf("FAIL: UmaCreate before resource loaded returned non-null\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaCreate before resource returns null\n");
    }


    UmaDestroy(NULL);
    printf("PASS: UmaDestroy(NULL) did not crash\n");


    int32_t load_result = UmaLoadResource("C:\\nonexistent\\path");
    if (load_result == 0)
    {
        printf("FAIL: UmaLoadResource with bad path returned 0\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaLoadResource with bad path returns %d\n",
               (int)load_result);
    }


    int32_t dir_result = UmaSetUserDir(NULL);
    if (dir_result == 0)
    {
        printf("FAIL: UmaSetUserDir(NULL) returned 0\n");
        ++failures;
    }
    else
    {
        printf("PASS: UmaSetUserDir(NULL) returns %d\n", (int)dir_result);
    }

    if (failures == 0)
    {
        printf("\nAll ABI smoke tests PASSED\n");
    }
    else
    {
        printf("\n%d ABI smoke test(s) FAILED\n", failures);
    }

    return failures;
}



int main(void)
{
    int failures = run_abi_smoke_test();
    return failures > 0 ? 1 : 0;
}
