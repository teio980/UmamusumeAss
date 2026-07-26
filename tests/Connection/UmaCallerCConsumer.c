//
// UmaCallerCConsumer.c — Minimal C consumer that verifies the DLL's
// C ABI compiles and links correctly from a pure-C translation unit.
//
// This file is compiled as C (not C++) and links to UmamusumeCore.dll's
// import library.
//

#include "UmaAssistant/UmaCaller.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* =========================================================================
 * Callback — records the first message and sets a flag
 * ========================================================================= */

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
        strncpy_s(ctx->first_json, sizeof(ctx->first_json),
                  details_json, _TRUNCATE);
    }

    if (message == UMA_MSG_CONNECTION_SUCCEEDED
        || message == UMA_MSG_CONNECTION_FAILED)
    {
        ctx->terminal_seen = 1;
    }

    (void)0; /* suppress empty-statement warning */
}

/* =========================================================================
 * ABI smoke test — minimal validation of exported symbols
 * ========================================================================= */

int run_abi_smoke_test(void)
{
    int failures = 0;

    /* 1. UmaGetVersion returns a string */
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

    /* 2. UmaCreate returns null before resource loaded */
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

    /* 3. UmaDestroy with null handle is safe */
    UmaDestroy(NULL);
    printf("PASS: UmaDestroy(NULL) did not crash\n");

    /* 4. UmaLoadResource with bad path returns nonzero */
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

    /* 5. UmaSetUserDir with null returns nonzero */
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

/* ========================================================================= */

int main(void)
{
    int failures = run_abi_smoke_test();
    return failures > 0 ? 1 : 0;
}
