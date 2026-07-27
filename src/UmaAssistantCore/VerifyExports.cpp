#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <array>
#include <cstddef>
#include <cstdio>
#include <cstring>

static constexpr std::array<char const*, 15> kExpectedExports = {
    "UmaGetVersion",
    "UmaCreate",
    "UmaDestroy",
    "UmaSetUserDir",
    "UmaLoadResource",
    "UmaConnectAsync",
    "UmaCancelConnect",
    "UmaCancelOperation",
    "UmaVerifyGameAsync",
    "UmaCaptureAsync",
    "UmaGetFramePngSize",
    "UmaCopyFramePng",
    "UmaReleaseFrame",
    "UmaTapAsync",
    "UmaSwipeAsync",
};

// ── Bounded RVA access helpers ─────────────────────────────────────────────

static void const* rva_ptr(void const* base, DWORD image_size, DWORD rva, DWORD size)
{
    if (rva >= image_size) return nullptr;
    if (size > image_size - rva) return nullptr;
    return static_cast<std::byte const*>(base) + rva;
}

static char const* rva_str(void const* base, DWORD image_size, DWORD rva)
{
    auto const* start = static_cast<char const*>(
        rva_ptr(base, image_size, rva, 1));
    if (!start) return nullptr;
    auto const* bytes = static_cast<std::byte const*>(base);
    DWORD offset = rva;
    while (offset < image_size && bytes[offset] != std::byte{0}) ++offset;
    if (offset >= image_size) return nullptr;
    return start;
}

// ── Verifier ───────────────────────────────────────────────────────────────

static bool verify_exact_export_table(void const* base, DWORD image_size)
{
    // DOS header
    auto const* dos = static_cast<IMAGE_DOS_HEADER const*>(
        rva_ptr(base, image_size, 0, sizeof(IMAGE_DOS_HEADER)));
    if (!dos || dos->e_magic != IMAGE_DOS_SIGNATURE)
    {
        std::puts("FAIL: invalid DOS header");
        return false;
    }

    // NT headers
    auto const e_lfanew = static_cast<DWORD>(dos->e_lfanew);
    auto const* nt = static_cast<IMAGE_NT_HEADERS const*>(
        rva_ptr(base, image_size, e_lfanew, sizeof(IMAGE_NT_HEADERS)));
    if (!nt || nt->Signature != IMAGE_NT_SIGNATURE
        || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR_MAGIC)
    {
        std::puts("FAIL: invalid NT headers");
        return false;
    }

    // Export directory
    auto const& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (dir.VirtualAddress == 0 || dir.Size == 0)
    {
        std::puts("FAIL: no export directory");
        return false;
    }

    if (!rva_ptr(base, image_size, dir.VirtualAddress, dir.Size))
    {
        std::puts("FAIL: export directory range out of bounds");
        return false;
    }

    auto const* exports = static_cast<IMAGE_EXPORT_DIRECTORY const*>(
        rva_ptr(base, image_size, dir.VirtualAddress, sizeof(IMAGE_EXPORT_DIRECTORY)));
    if (!exports)
    {
        std::puts("FAIL: export directory out of range");
        return false;
    }

    // Count check
    if (exports->NumberOfFunctions != kExpectedExports.size()
        || exports->NumberOfNames != kExpectedExports.size())
    {
        std::printf(
            "FAIL: expected %zu functions and names, found %lu functions and %lu names\n",
            kExpectedExports.size(),
            static_cast<unsigned long>(exports->NumberOfFunctions),
            static_cast<unsigned long>(exports->NumberOfNames));
        return false;
    }

    auto const num_functions = exports->NumberOfFunctions;
    auto const num_names = exports->NumberOfNames;

    // Validate address tables fit within the image
    auto const* func_rvas = static_cast<DWORD const*>(
        rva_ptr(base, image_size, exports->AddressOfFunctions,
                num_functions * sizeof(DWORD)));
    auto const* name_rvas = static_cast<DWORD const*>(
        rva_ptr(base, image_size, exports->AddressOfNames,
                num_names * sizeof(DWORD)));
    auto const* ordinals = static_cast<WORD const*>(
        rva_ptr(base, image_size, exports->AddressOfNameOrdinals,
                num_names * sizeof(WORD)));

    if (!func_rvas || !name_rvas || !ordinals)
    {
        std::puts("FAIL: export address tables out of range");
        return false;
    }

    // Validate each named export
    std::array<bool, kExpectedExports.size()> found{};
    std::array<bool, 256> ord_seen{};
    bool valid = true;

    for (DWORD i = 0; i < num_names; ++i)
    {
        // Name string — must be null-terminated within the image
        auto const* name = rva_str(base, image_size, name_rvas[i]);
        if (!name)
        {
            std::printf("FAIL: name %lu has invalid or unterminated RVA\n",
                        static_cast<unsigned long>(i));
            valid = false;
            continue;
        }

        // Ordinal — must be in range
        WORD const ord = ordinals[i];
        if (ord >= num_functions)
        {
            std::printf("FAIL: name '%s' has out-of-range ordinal %u\n", name, ord);
            valid = false;
            continue;
        }

        // Ordinal — must not be duplicated
        if (ord_seen[ord])
        {
            std::printf("FAIL: duplicate ordinal %u for name '%s'\n", ord, name);
            valid = false;
            continue;
        }
        ord_seen[ord] = true;

        DWORD const func_rva = func_rvas[ord];
        if (func_rva == 0 || !rva_ptr(base, image_size, func_rva, 1))
        {
            std::printf("FAIL: name '%s' (ordinal %u) has invalid function RVA\n",
                        name, ord);
            valid = false;
            continue;
        }

        // Function RVA — must not be forwarded (point into export directory)
        if (func_rva >= dir.VirtualAddress
            && func_rva - dir.VirtualAddress < dir.Size)
        {
            std::printf("FAIL: name '%s' (ordinal %u) is a forwarded export\n",
                        name, ord);
            valid = false;
            continue;
        }

        // Match against expected names
        bool expected = false;
        for (std::size_t j = 0; j < kExpectedExports.size(); ++j)
        {
            if (std::strcmp(name, kExpectedExports[j]) == 0)
            {
                found[j] = true;
                expected = true;
                break;
            }
        }
        if (!expected)
        {
            std::printf("FAIL: unexpected or decorated export '%s'\n", name);
            valid = false;
        }
    }

    // Check for missing expected exports
    for (std::size_t i = 0; i < found.size(); ++i)
    {
        if (!found[i])
        {
            std::printf("FAIL: export table omitted '%s'\n", kExpectedExports[i]);
            valid = false;
        }
    }

    return valid;
}

// ── Entry point (wide path, no DllMain) ────────────────────────────────────

int wmain(int argc, wchar_t const* const* argv);

int wmain(int argc, wchar_t const* const* argv)
{
    if (argc != 2)
    {
        std::puts("usage: UmaExportVerification <dll-path>");
        return 2;
    }

    HANDLE const hFile = CreateFileW(
        argv[1], GENERIC_READ, FILE_SHARE_READ, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE)
    {
        std::printf("FAIL: cannot open '%ls' (error %lu)\n",
                    argv[1], GetLastError());
        return 1;
    }

    LARGE_INTEGER file_size{};
    if (!GetFileSizeEx(hFile, &file_size)
        || file_size.QuadPart < static_cast<LONGLONG>(sizeof(IMAGE_DOS_HEADER))
        || file_size.QuadPart > static_cast<LONGLONG>(MAXDWORD))
    {
        DWORD const err = GetLastError();
        CloseHandle(hFile);
        std::printf("FAIL: cannot determine a valid size for '%ls' (error %lu)\n",
                    argv[1], err);
        return 1;
    }

    HANDLE const hMapping = CreateFileMappingW(
        hFile, nullptr, PAGE_READONLY | SEC_IMAGE, 0, 0, nullptr);
    if (hMapping == nullptr)
    {
        DWORD const err = GetLastError();
        CloseHandle(hFile);
        std::printf("FAIL: cannot map '%ls' (error %lu)\n", argv[1], err);
        return 1;
    }

    void const* const view = MapViewOfFile(hMapping, FILE_MAP_READ, 0, 0, 0);
    if (view == nullptr)
    {
        DWORD const err = GetLastError();
        CloseHandle(hMapping);
        CloseHandle(hFile);
        std::printf("FAIL: cannot view '%ls' (error %lu)\n", argv[1], err);
        return 1;
    }

    DWORD image_size = 0;
    {
        DWORD const raw_size = static_cast<DWORD>(file_size.QuadPart);
        auto const* dos = static_cast<IMAGE_DOS_HEADER const*>(
            rva_ptr(view, raw_size, 0, sizeof(IMAGE_DOS_HEADER)));
        if (dos->e_magic == IMAGE_DOS_SIGNATURE && dos->e_lfanew > 0)
        {
            auto const* nt = static_cast<IMAGE_NT_HEADERS const*>(
                rva_ptr(view, raw_size, static_cast<DWORD>(dos->e_lfanew),
                        sizeof(IMAGE_NT_HEADERS)));
            if (nt && nt->Signature == IMAGE_NT_SIGNATURE
                && nt->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR_MAGIC)
                image_size = nt->OptionalHeader.SizeOfImage;
        }
    }

    bool const valid = verify_exact_export_table(view, image_size);

    UnmapViewOfFile(view);
    CloseHandle(hMapping);
    CloseHandle(hFile);

    if (!valid) return 1;

    std::puts("PASS: exactly 15 undecorated C ABI exports");
    return 0;
}
