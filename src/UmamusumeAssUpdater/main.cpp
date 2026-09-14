#include <windows.h>
#include <bcrypt.h>

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <filesystem>
#include <functional>
#include <fstream>
#include <optional>
#include <set>
#include <string>
#include <utility>
#include <vector>

namespace fs = std::filesystem;
using json = nlohmann::json;

namespace {

constexpr std::array<unsigned char, 32> kPublicX = {
    0x5d, 0x27, 0xeb, 0x82, 0xe4, 0x15, 0xbd, 0x5a,
    0xfe, 0x0c, 0x9b, 0x94, 0xde, 0x3f, 0x19, 0x37,
    0x4f, 0xe6, 0x66, 0x03, 0xaf, 0x7f, 0xde, 0x09,
    0x55, 0x3f, 0x6e, 0x09, 0xeb, 0x92, 0x71, 0x6b,
};
constexpr std::array<unsigned char, 32> kPublicY = {
    0x41, 0x61, 0x9b, 0x6e, 0x6e, 0xa3, 0x84, 0x24,
    0xad, 0x04, 0x48, 0xe3, 0xfc, 0x3e, 0x00, 0xb3,
    0x4a, 0x48, 0xa1, 0xf3, 0x28, 0xc6, 0x9d, 0x68,
    0xd8, 0xd5, 0x6d, 0x38, 0xff, 0xf4, 0x31, 0xa9,
};

struct Args {
    DWORD parentPid = 0;
    fs::path installRoot;
    fs::path stagingRoot;
    fs::path backupRoot;
    fs::path planPath;
    fs::path statusPath;
    std::string operationId;
};

struct JournalEntry {
    fs::path target;
    fs::path backup;
    bool existed = false;
    bool installed = false;
};

std::wstring ToWide(std::string const& value)
{
    if (value.empty()) return {};
    int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                     value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (length <= 0) return {};
    std::wstring result(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
                        static_cast<int>(value.size()), result.data(), length);
    return result;
}

std::string ToUtf8(std::wstring const& value)
{
    if (value.empty()) return {};
    int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                                     static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (length <= 0) return {};
    std::string result(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                        static_cast<int>(value.size()), result.data(), length, nullptr, nullptr);
    return result;
}

bool IsSafeRelative(std::string const& value)
{
    if (value.empty() || value.find('\0') != std::string::npos || value.find(':') != std::string::npos)
        return false;
    if (value.front() == '/' || value.front() == '\\') return false;
    std::string component;
    for (char character : value) {
        if (character == '\\') component.push_back('/');
        else component.push_back(character);
    }
    size_t start = 0;
    while (start <= component.size()) {
        auto end = component.find('/', start);
        if (end == std::string::npos) end = component.size();
        auto part = component.substr(start, end - start);
        if (part.empty() || part == "." || part == "..") return false;
        start = end + 1;
        if (end == component.size()) break;
    }
    return true;
}

std::optional<fs::path> ResolveUnder(fs::path const& root, std::string const& relative)
{
    if (!IsSafeRelative(relative)) return std::nullopt;
    std::error_code error;
    auto canonicalRoot = fs::weakly_canonical(root, error);
    if (error) return std::nullopt;
    auto utf8 = std::u8string(reinterpret_cast<char8_t const*>(relative.data()), relative.size());
    auto candidate = fs::weakly_canonical(canonicalRoot / fs::path(utf8), error);
    if (error) return std::nullopt;
    auto rootText = canonicalRoot.wstring();
    auto candidateText = candidate.wstring();
    if (!rootText.empty() && rootText.back() != L'\\') rootText.push_back(L'\\');
    if (candidateText.size() < rootText.size()
        || _wcsnicmp(candidateText.c_str(), rootText.c_str(), rootText.size()) != 0)
        return std::nullopt;

    for (auto current = candidate; current != canonicalRoot && current.has_parent_path(); current = current.parent_path()) {
        auto attributes = GetFileAttributesW(current.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            return std::nullopt;
        if (current.parent_path() == canonicalRoot) break;
    }
    return candidate;
}

std::optional<std::vector<unsigned char>> DecodeBase64(std::string const& input)
{
    static constexpr char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::vector<unsigned char> output;
    int value = 0;
    int bits = -8;
    for (unsigned char character : input) {
        if (std::isspace(character) != 0) continue;
        if (character == '=') break;
        auto position = std::find(std::begin(alphabet), std::end(alphabet) - 1, character);
        if (position == std::end(alphabet) - 1) return std::nullopt;
        value = (value << 6) + static_cast<int>(position - std::begin(alphabet));
        bits += 6;
        if (bits >= 0) {
            output.push_back(static_cast<unsigned char>((value >> bits) & 0xff));
            bits -= 8;
        }
    }
    return output;
}

std::optional<std::array<unsigned char, 32>> Sha256File(fs::path const& path)
{
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    DWORD objectLength = 0;
    DWORD resultLength = 0;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0
        || BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
                             reinterpret_cast<unsigned char*>(&objectLength), sizeof(objectLength),
                             &resultLength, 0) != 0)
        return std::nullopt;
    std::vector<unsigned char> object(objectLength);
    if (BCryptCreateHash(algorithm, &hash, object.data(), objectLength, nullptr, 0, 0) != 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0);
        return std::nullopt;
    }
    std::ifstream input(path, std::ios::binary);
    std::array<char, 128 * 1024> buffer{};
    while (input.read(buffer.data(), static_cast<std::streamsize>(buffer.size())) || input.gcount() > 0) {
        auto count = static_cast<ULONG>(input.gcount());
        if (BCryptHashData(hash, reinterpret_cast<unsigned char*>(buffer.data()), count, 0) != 0) {
            BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0); return std::nullopt;
        }
    }
    std::array<unsigned char, 32> result{};
    bool ok = input.good() || input.eof();
    if (ok) ok = BCryptFinishHash(hash, result.data(), static_cast<ULONG>(result.size()), 0) == 0;
    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return ok ? std::optional(result) : std::nullopt;
}

std::string Hex(std::array<unsigned char, 32> const& value);

bool HashAppendFile(BCRYPT_HASH_HANDLE hash, fs::path const& path)
{
    std::ifstream input(path, std::ios::binary);
    if (!input) return false;
    std::array<char, 128 * 1024> buffer{};
    while (input.read(buffer.data(), static_cast<std::streamsize>(buffer.size()))
           || input.gcount() > 0) {
        auto count = static_cast<ULONG>(input.gcount());
        if (BCryptHashData(hash, reinterpret_cast<unsigned char*>(buffer.data()), count, 0) != 0)
            return false;
    }
    return input.good() || input.eof();
}

bool HashAppendBytes(BCRYPT_HASH_HANDLE hash, std::string const& bytes)
{
    return bytes.size() <= static_cast<size_t>(ULONG_MAX)
        && BCryptHashData(hash,
                          const_cast<unsigned char*>(
                              reinterpret_cast<unsigned char const*>(bytes.data())),
                          static_cast<ULONG>(bytes.size()), 0) == 0;
}

bool VerifyManagedTree(fs::path const& root, json const& asset, std::string const& expected)
{
    if (expected.empty() || !asset.contains("files") || !asset["files"].is_array()
        || asset["files"].size() > 10'000)
        return false;
    std::vector<json> files;
    for (auto const& item : asset["files"]) {
        if (!item.is_object() || !item.contains("path") || !item["path"].is_string()
            || !item.contains("size") || !item["size"].is_number_integer()) return false;
        files.push_back(item);
    }
    std::sort(files.begin(), files.end(), [](json const& left, json const& right) {
        return left.value("path", "") < right.value("path", "");
    });

    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    DWORD objectLength = 0;
    DWORD resultLength = 0;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0
        || BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
                             reinterpret_cast<unsigned char*>(&objectLength),
                             sizeof(objectLength), &resultLength, 0) != 0) {
        if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
        return false;
    }
    std::vector<unsigned char> object(objectLength);
    if (BCryptCreateHash(algorithm, &hash, object.data(), objectLength, nullptr, 0, 0) != 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0);
        return false;
    }
    bool valid = true;
    for (auto const& item : files) {
        auto relative = item.value("path", "");
        auto path = ResolveUnder(root, relative);
        if (!path || !fs::is_regular_file(*path)) { valid = false; break; }
        std::error_code error;
        auto size = fs::file_size(*path, error);
        auto expectedSize = item["size"].get<long long>();
        auto fileHash = Sha256File(*path);
        if (error || expectedSize < 0 || size != static_cast<uintmax_t>(expectedSize)
            || !fileHash || Hex(*fileHash) != item.value("sha256", "")) {
            valid = false;
            break;
        }
        auto normalized = relative;
        std::replace(normalized.begin(), normalized.end(), '\\', '/');
        if (!HashAppendBytes(hash, normalized) || !HashAppendBytes(hash, std::string(1, '\0'))
            || !HashAppendFile(hash, *path)) {
            valid = false;
            break;
        }
    }
    std::array<unsigned char, 32> digest{};
    if (valid)
        valid = BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) == 0
            && Hex(digest) == expected;
    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return valid;
}

std::string Hex(std::array<unsigned char, 32> const& value)
{
    static constexpr char digits[] = "0123456789ABCDEF";
    std::string result;
    result.reserve(64);
    for (auto byte : value) { result.push_back(digits[byte >> 4]); result.push_back(digits[byte & 15]); }
    return result;
}

bool VerifyManifest(fs::path const& manifestPath, fs::path const& signaturePath)
{
    std::ifstream manifest(manifestPath, std::ios::binary);
    std::string bytes((std::istreambuf_iterator<char>(manifest)), std::istreambuf_iterator<char>());
    std::ifstream signature(signaturePath);
    std::string signatureText((std::istreambuf_iterator<char>(signature)), std::istreambuf_iterator<char>());
    auto decoded = DecodeBase64(signatureText);
    if (!decoded || decoded->size() != 64) return false;

    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_KEY_HANDLE key = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_ECDSA_P256_ALGORITHM, nullptr, 0) != 0)
        return false;
    struct PublicBlob {
        BCRYPT_ECCKEY_BLOB header{ BCRYPT_ECDSA_PUBLIC_P256_MAGIC, 32 };
        std::array<unsigned char, 32> x = kPublicX;
        std::array<unsigned char, 32> y = kPublicY;
    } blob;
    bool ok = BCryptImportKeyPair(algorithm, nullptr, BCRYPT_ECCPUBLIC_BLOB,
                                  &key, reinterpret_cast<unsigned char*>(&blob), sizeof(blob), 0) == 0;
    auto digest = Sha256File(manifestPath);
    if (ok && digest) {
        ok = BCryptVerifySignature(key, nullptr, digest->data(), static_cast<ULONG>(digest->size()),
                                   decoded->data(), static_cast<ULONG>(decoded->size()), 0) == 0;
    } else ok = false;
    if (key) BCryptDestroyKey(key);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return ok;
}

bool IsExactPath(fs::path const& actual, fs::path const& expected)
{
    std::error_code error;
    auto a = fs::weakly_canonical(actual, error);
    if (error) return false;
    auto e = fs::weakly_canonical(expected, error);
    return !error && _wcsicmp(a.c_str(), e.c_str()) == 0;
}

bool IsPathUnder(fs::path const& child, fs::path const& root)
{
    std::error_code error;
    auto canonicalRoot = fs::weakly_canonical(root, error);
    if (error) return false;
    auto canonicalChild = fs::weakly_canonical(child, error);
    if (error) return false;
    auto rootText = canonicalRoot.wstring();
    auto childText = canonicalChild.wstring();
    if (!rootText.empty() && rootText.back() != L'\\') rootText.push_back(L'\\');
    return childText.size() >= rootText.size()
        && _wcsnicmp(childText.c_str(), rootText.c_str(), rootText.size()) == 0;
}

bool WriteText(fs::path const& path, std::string const& text)
{
    std::error_code error;
    fs::create_directories(path.parent_path(), error);
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    output << text;
    return output.good();
}

bool ParentHasExited(DWORD pid)
{
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (!process) return GetLastError() == ERROR_INVALID_PARAMETER;
    auto result = WaitForSingleObject(process, 0);
    CloseHandle(process);
    return result == WAIT_OBJECT_0;
}

bool RelaunchInstalledApp(Args const& args)
{
    auto executable = args.installRoot / L"UmamusumeAss.exe";
    if (!fs::is_regular_file(executable)) return false;

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    std::wstring command = L"\"" + executable.wstring() + L"\"";
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr,
                        args.installRoot.wstring().c_str(), &startup, &process))
        return false;
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return true;
}

void WriteFailure(Args const& args, std::string const& reason)
{
    bool parentExited = ParentHasExited(args.parentPid);
    WriteText(args.statusPath, "failed\n" + reason);
    MessageBoxW(nullptr, ToWide(reason).c_str(), L"UmamusumeAss 更新失败", MB_OK | MB_ICONERROR);
    if (parentExited)
        RelaunchInstalledApp(args);
}

bool PathExists(fs::path const& path)
{
    return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

bool RetryFileOperation(std::function<bool()> const& operation,
                        int maxAttempts = 5,
                        DWORD initialDelayMilliseconds = 200)
{
    for (int attempt = 1; attempt <= maxAttempts; ++attempt) {
        if (operation()) return true;
        auto error = GetLastError();
        if (error != ERROR_SHARING_VIOLATION
            && error != ERROR_LOCK_VIOLATION
            && error != ERROR_ACCESS_DENIED)
            return false;
        if (attempt < maxAttempts)
            Sleep(initialDelayMilliseconds * static_cast<DWORD>(1 << (attempt - 1)));
    }
    return false;
}

fs::path RenameLockedFile(fs::path const& path)
{
    static volatile LONG counter = 0;
    auto renamed = path;
    renamed += L"." + std::to_wstring(GetTickCount64()) + L"."
        + std::to_wstring(InterlockedIncrement(&counter)) + L".pendingdelete";
    for (int attempt = 0; attempt < 10; ++attempt) {
        if (MoveFileExW(path.c_str(), renamed.c_str(), MOVEFILE_REPLACE_EXISTING))
            return renamed;
        Sleep(100);
        renamed += L"." + std::to_wstring(attempt);
    }
    return {};
}

bool ForceDeleteFile(fs::path const& path)
{
    if (RetryFileOperation([&] { return DeleteFileW(path.c_str()) != FALSE; }))
        return true;
    if (!PathExists(path)) return true;

    auto renamed = RenameLockedFile(path);
    if (!renamed.empty()) {
        RetryFileOperation(
            [&] { return DeleteFileW(renamed.c_str()) != FALSE; }, 3, 500);
        return true;
    }

    return MoveFileExW(path.c_str(), nullptr, MOVEFILE_DELAY_UNTIL_REBOOT) != FALSE;
}

bool ForceRemoveDirectoryRecursive(fs::path const& directory)
{
    if (!PathExists(directory)) return true;
    auto pattern = directory / L"*";
    WIN32_FIND_DATAW data{};
    HANDLE find = FindFirstFileW(pattern.c_str(), &data);
    if (find != INVALID_HANDLE_VALUE) {
        do {
            if (wcscmp(data.cFileName, L".") == 0
                || wcscmp(data.cFileName, L"..") == 0)
                continue;
            auto child = directory / data.cFileName;
            if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                ForceRemoveDirectoryRecursive(child);
            else
                ForceDeleteFile(child);
        } while (FindNextFileW(find, &data));
        FindClose(find);
    }
    return RemoveDirectoryW(directory.c_str()) != FALSE || !PathExists(directory);
}

bool WaitForParent(DWORD pid)
{
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (!process) return GetLastError() == ERROR_INVALID_PARAMETER;
    auto result = WaitForSingleObject(process, INFINITE);
    CloseHandle(process);
    return result == WAIT_OBJECT_0;
}

bool MoveToBackup(fs::path const& target, fs::path const& backup)
{
    if (!fs::exists(target)) return true;
    std::error_code error;
    fs::create_directories(backup.parent_path(), error);
    if (error) return false;
    // The operation directory is under LocalAppData and may be on a
    // different volume from the portable installation. Copy+delete works
    // across volumes and is journaled just like a same-volume move.
    if (!CopyFileW(target.c_str(), backup.c_str(), FALSE)) return false;
    if (!DeleteFileW(target.c_str())) {
        DeleteFileW(backup.c_str());
        return false;
    }
    return !fs::exists(target) && fs::exists(backup);
}

bool InstallAtomic(fs::path const& source, fs::path const& target)
{
    std::error_code error;
    fs::create_directories(target.parent_path(), error);
    if (error || !fs::is_regular_file(source, error)) return false;
    auto temporary = target;
    temporary += L".updating";
    DeleteFileW(temporary.c_str());
    if (!CopyFileW(source.c_str(), temporary.c_str(), FALSE)) return false;
    if (!MoveFileExW(temporary.c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(temporary.c_str());
        return false;
    }
    return true;
}

void Rollback(std::vector<JournalEntry> const& journal)
{
    for (auto it = journal.rbegin(); it != journal.rend(); ++it) {
        if (it->installed) DeleteFileW(it->target.c_str());
        if (it->existed && fs::exists(it->backup)) {
            CopyFileW(it->backup.c_str(), it->target.c_str(), FALSE);
            DeleteFileW(it->backup.c_str());
        }
    }
}

std::optional<Args> ParseArgs(int argc, wchar_t** argv)
{
    Args result;
    auto get = [&](wchar_t const* name) -> std::optional<std::wstring> {
        for (int index = 1; index + 1 < argc; ++index)
            if (_wcsicmp(argv[index], name) == 0) return std::wstring(argv[index + 1]);
        return std::nullopt;
    };
    auto parent = get(L"--parent-pid");
    auto root = get(L"--install-root");
    auto staging = get(L"--staging-root");
    auto backup = get(L"--backup-root");
    auto plan = get(L"--plan");
    auto status = get(L"--status");
    auto operation = get(L"--operation-id");
    if (!parent || !root || !staging || !backup || !plan || !status || !operation) return std::nullopt;
    result.parentPid = static_cast<DWORD>(_wtoi(parent->c_str()));
    if (result.parentPid == 0) return std::nullopt;
    result.installRoot = fs::path(*root);
    result.stagingRoot = fs::path(*staging);
    result.backupRoot = fs::path(*backup);
    result.planPath = fs::path(*plan);
    result.statusPath = fs::path(*status);
    result.operationId = ToUtf8(*operation);
    return result;
}

int Run(Args const& args)
{
    HANDLE mutex = CreateMutexW(nullptr, TRUE, L"Local\\UmamusumeAss.Updater.v1");
    if (!mutex || GetLastError() == ERROR_ALREADY_EXISTS) {
        if (mutex) CloseHandle(mutex);
        WriteFailure(args, "已有另一个更新操作正在运行。");
        return 2;
    }
    struct MutexGuard {
        HANDLE handle;
        ~MutexGuard() { if (handle) CloseHandle(handle); }
    } mutexGuard{ mutex };
    if (!args.installRoot.is_absolute() || !fs::is_directory(args.installRoot)) {
        WriteFailure(args, "安装目录无效。"); return 2;
    }
    std::ifstream planInput(args.planPath);
    json plan;
    try { planInput >> plan; } catch (...) { WriteFailure(args, "更新计划无效。"); return 2; }
    planInput.close();
    auto manifestPath = fs::path(plan.value("manifestPath", ""));
    auto signaturePath = fs::path(plan.value("signaturePath", ""));
    if (manifestPath.empty()) manifestPath = args.planPath.parent_path() / "manifest.json";
    if (signaturePath.empty()) signaturePath = args.planPath.parent_path() / "manifest.sig";
    if (!IsExactPath(manifestPath, args.planPath.parent_path() / "manifest.json")
        || !IsExactPath(signaturePath, args.planPath.parent_path() / "manifest.sig")) {
        WriteFailure(args, "更新清单必须位于本次操作目录。"); return 2;
    }
    if (!IsExactPath(args.statusPath.parent_path(), args.planPath.parent_path())
        || !IsPathUnder(args.stagingRoot, args.planPath.parent_path())
        || !IsPathUnder(args.backupRoot, args.planPath.parent_path())) {
        WriteFailure(args, "更新操作目录约束无效。"); return 2;
    }
    if (!VerifyManifest(manifestPath, signaturePath)) {
        WriteFailure(args, "更新清单签名验证失败。"); return 2;
    }
    std::ifstream signedInput(manifestPath);
    json signedManifest;
    try { signedInput >> signedManifest; } catch (...) {
        WriteFailure(args, "更新清单 JSON 无效。"); return 2;
    }
    signedInput.close();
    if (plan.value("operationId", "") != args.operationId) {
        WriteFailure(args, "更新操作 ID 不匹配。"); return 2;
    }
    auto replace = plan.value("replace", json::array());
    auto deletes = plan.value("deletes", json::array());
    auto manifest = plan.value("manifest", json::object());
    auto asset = manifest.value("selectedAsset", json::object());
    json signedAsset;
    for (auto const& candidate : signedManifest.value("assets", json::array())) {
        if (candidate.value("assetName", "") == asset.value("assetName", "")) {
            signedAsset = candidate;
            break;
        }
    }
    if (signedAsset.is_null() || signedAsset != asset) {
        WriteFailure(args, "选中的更新 asset 与签名清单不一致。"); return 2;
    }
    std::set<std::string> manifestPaths;
    for (auto const& item : asset.value("files", json::array())) manifestPaths.insert(item.value("path", ""));
    std::set<std::string> planPaths;
    for (auto const& item : replace) planPaths.insert(item.value("path", ""));
    if (manifestPaths != planPaths || asset.value("deletes", json::array()) != deletes) {
        WriteFailure(args, "更新计划与签名清单不一致。"); return 2;
    }

    json targetFull;
    for (auto const& candidate : signedManifest.value("assets", json::array())) {
        if (candidate.value("type", "") == "full") {
            targetFull = candidate;
            break;
        }
    }
    if (targetFull.is_null()) {
        WriteFailure(args, "签名清单缺少完整目标 inventory。"); return 2;
    }
    if (asset.value("type", "") == "full" && !deletes.empty()) {
        WriteFailure(args, "完整更新不允许携带删除清单。"); return 2;
    }
    if (asset.value("type", "") == "delta") {
        json currentManifestText = plan.contains("currentManifestPath")
            ? plan["currentManifestPath"] : json();
        json currentSignatureText = plan.contains("currentSignaturePath")
            ? plan["currentSignaturePath"] : json();
        if (!currentManifestText.is_string() || !currentSignatureText.is_string()) {
            WriteFailure(args, "程序增量更新缺少当前版本清单。"); return 2;
        }
        auto currentManifestPath = fs::path(ToWide(currentManifestText.get<std::string>()));
        auto currentSignaturePath = fs::path(ToWide(currentSignatureText.get<std::string>()));
        if (!IsExactPath(currentManifestPath, args.planPath.parent_path() / "source-manifest.json")
            || !IsExactPath(currentSignaturePath, args.planPath.parent_path() / "source-manifest.sig")
            || !VerifyManifest(currentManifestPath, currentSignaturePath)) {
            WriteFailure(args, "当前版本清单签名验证失败。"); return 2;
        }
        std::ifstream currentInput(currentManifestPath);
        json currentManifest;
        try { currentInput >> currentManifest; } catch (...) {
            WriteFailure(args, "当前版本清单 JSON 无效。"); return 2;
        }
        json currentFull;
        for (auto const& candidate : currentManifest.value("assets", json::array())) {
            if (candidate.value("type", "") == "full") {
                currentFull = candidate;
                break;
            }
        }
        auto sourceTree = asset.value("sourceTreeSha256", "");
        auto sourceManifest = asset.value("sourceManifestSha256", "");
        auto currentTree = plan.value("currentTreeSha256", "");
        auto currentManifestBytes = Sha256File(currentManifestPath);
        if (currentFull.is_null() || sourceTree.empty() || sourceManifest.empty()
            || !currentManifestBytes || Hex(*currentManifestBytes) != sourceManifest
            || currentTree != sourceTree
            || !VerifyManagedTree(args.installRoot, currentFull, sourceTree)) {
            WriteFailure(args, "安装目录不符合增量更新的签名基线。"); return 2;
        }
        std::set<std::string> currentPaths;
        for (auto const& item : currentFull.value("files", json::array()))
            currentPaths.insert(item.value("path", ""));
        for (auto const& item : deletes) {
            if (!item.is_string() || !IsSafeRelative(item.get<std::string>())
                || !currentPaths.contains(item.get<std::string>())) {
                WriteFailure(args, "增量删除清单超出当前受管 inventory。"); return 2;
            }
        }
    }

    if (!WaitForParent(args.parentPid)) { WriteFailure(args, "无法等待主程序退出。"); return 2; }
    fs::create_directories(args.backupRoot);
    std::vector<JournalEntry> journal;
    auto fail = [&](std::string const& reason) { Rollback(journal); WriteFailure(args, reason); return 2; };

    for (auto const& item : replace) {
        auto relative = item.value("path", "");
        auto source = ResolveUnder(args.stagingRoot, relative);
        auto target = ResolveUnder(args.installRoot, relative);
        auto backup = ResolveUnder(args.backupRoot, relative);
        if (!source || !target || !backup) return fail("更新清单包含非法路径。");
        auto digest = Sha256File(*source);
        if (!digest || Hex(*digest) != item.value("sha256", "")) return fail("更新文件校验失败。");
        JournalEntry entry{ *target, *backup, fs::exists(*target), false };
        journal.push_back(entry);
        auto& recorded = journal.back();
        if (recorded.existed && !MoveToBackup(recorded.target, recorded.backup)) return fail("备份旧文件失败。");
        if (!InstallAtomic(*source, recorded.target)) return fail("安装更新文件失败。");
        recorded.installed = true;
    }
    for (auto const& item : deletes) {
        auto target = ResolveUnder(args.installRoot, item.get<std::string>());
        auto backup = ResolveUnder(args.backupRoot, item.get<std::string>());
        if (!target || !backup) return fail("删除清单包含非法路径。");
        JournalEntry entry{ *target, *backup, fs::exists(*target), false };
        journal.push_back(entry);
        auto& recorded = journal.back();
        if (recorded.existed && !MoveToBackup(recorded.target, recorded.backup)) return fail("备份待删除文件失败。");
        recorded.installed = true;
    }
    if (!VerifyManagedTree(args.installRoot, targetFull,
                           targetFull.value("targetTreeSha256", "")))
        return fail("更新后文件与目标签名 inventory 不一致。");

    // Same finish order as MAA: remove the downloaded update data first,
    // then launch the installed application. This updater runs outside the
    // updates directory, so it never tries to delete itself.
    auto updatesRoot = args.statusPath.parent_path().parent_path();
    ForceRemoveDirectoryRecursive(updatesRoot);
    if (!RelaunchInstalledApp(args)) {
        MessageBoxW(nullptr,
            L"更新已完成，但重新启动程序失败。请手动启动 UmamusumeAss。",
            L"UmamusumeAss", MB_OK | MB_ICONERROR);
        return 2;
    }
    return 0;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    auto args = ParseArgs(argc, argv);
    if (!args) {
        MessageBoxW(nullptr, L"内部更新器参数无效。", L"UmamusumeAss", MB_OK | MB_ICONERROR);
        return 1;
    }
    return Run(*args);
}
