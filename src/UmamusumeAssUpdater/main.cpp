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
    bool isDirectory = false;
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

std::string Hex(std::array<unsigned char, 32> const& value)
{
    static constexpr char digits[] = "0123456789ABCDEF";
    std::string text;
    text.reserve(value.size() * 2);
    for (unsigned char byte : value) {
        text.push_back(digits[(byte >> 4) & 0x0F]);
        text.push_back(digits[byte & 0x0F]);
    }
    return text;
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

bool IsExactPath(fs::path const& left, fs::path const& right)
{
    std::error_code error;
    auto canonicalLeft = fs::weakly_canonical(left, error);
    if (error) return false;
    auto canonicalRight = fs::weakly_canonical(right, error);
    if (error) return false;
    return _wcsicmp(canonicalLeft.c_str(), canonicalRight.c_str()) == 0;
}

bool IsPathUnder(fs::path const& candidate, fs::path const& root)
{
    std::error_code error;
    auto canonicalCandidate = fs::weakly_canonical(candidate, error);
    if (error) return false;
    auto canonicalRoot = fs::weakly_canonical(root, error);
    if (error) return false;
    auto rootText = canonicalRoot.wstring();
    auto candidateText = canonicalCandidate.wstring();
    if (!rootText.empty() && rootText.back() != L'\\') rootText.push_back(L'\\');
    return candidateText.size() >= rootText.size()
        && _wcsnicmp(candidateText.c_str(), rootText.c_str(), rootText.size()) == 0;
}

bool WriteText(fs::path const& path, std::string const& content)
{
    std::error_code error;
    fs::create_directories(path.parent_path(), error);
    if (error) return false;
    std::ofstream stream(path, std::ios::trunc);
    stream << content;
    return stream.good();
}

bool ParentHasExited(DWORD pid)
{
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (!process) return true;
    auto wait = WaitForSingleObject(process, 0);
    CloseHandle(process);
    return wait != WAIT_TIMEOUT;
}

bool RelaunchInstalledApp(Args const& args)
{
    auto executable = args.installRoot / L"UmamusumeAss.exe";
    if (!fs::exists(executable)) return false;
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
    auto result = WaitForSingleObject(process, 60000);
    CloseHandle(process);
    return result == WAIT_OBJECT_0;
}

bool MoveToBackup(fs::path const& target, fs::path const& backup)
{
    if (!fs::exists(target)) return true;
    std::error_code error;
    fs::create_directories(backup.parent_path(), error);
    if (error) return false;
    if (!CopyFileW(target.c_str(), backup.c_str(), FALSE)) return false;
    if (!DeleteFileW(target.c_str())) {
        DeleteFileW(backup.c_str());
        return false;
    }
    return !fs::exists(target) && fs::exists(backup);
}

bool MoveDirectory(fs::path const& source, fs::path const& destination)
{
    std::error_code error;
    fs::create_directories(destination.parent_path(), error);
    if (error) return false;
    if (MoveFileExW(source.c_str(), destination.c_str(), MOVEFILE_COPY_ALLOWED | MOVEFILE_WRITE_THROUGH))
        return true;
    fs::copy(source, destination, fs::copy_options::recursive | fs::copy_options::overwrite_existing, error);
    if (error) return false;
    fs::remove_all(source, error);
    return !error;
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
        if (it->isDirectory) {
            if (it->installed) ForceRemoveDirectoryRecursive(it->target);
            if (it->existed && fs::exists(it->backup)) {
                MoveDirectory(it->backup, it->target);
            }
        } else {
            if (it->installed) DeleteFileW(it->target.c_str());
            if (it->existed && fs::exists(it->backup)) {
                CopyFileW(it->backup.c_str(), it->target.c_str(), FALSE);
                DeleteFileW(it->backup.c_str());
            }
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
    // Independent protocol version 2 mutex
    HANDLE mutex = CreateMutexW(nullptr, TRUE, L"Local\\UmamusumeAss.Updater.v2");
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

    if (plan.value("schemaVersion", 0) != 2) {
        WriteFailure(args, "更新计划协议版本不匹配。"); return 2;
    }
    if (plan.value("operationId", "") != args.operationId) {
        WriteFailure(args, "更新操作 ID 不匹配。"); return 2;
    }

    auto inventoryPath = args.planPath.parent_path() / "local-inventory.json";
    if (plan.contains("localInventoryPath")) {
        auto customPath = fs::path(ToWide(plan["localInventoryPath"].get<std::string>()));
        if (!customPath.empty()) inventoryPath = customPath;
    }

    if (!IsExactPath(args.statusPath.parent_path(), args.planPath.parent_path())
        || !IsPathUnder(args.stagingRoot, args.planPath.parent_path())
        || !IsPathUnder(args.backupRoot, args.planPath.parent_path())) {
        WriteFailure(args, "更新操作目录约束无效。"); return 2;
    }

    std::ifstream inventoryInput(inventoryPath);
    json inventory;
    try { inventoryInput >> inventory; } catch (...) {
        WriteFailure(args, "本地更新清单 JSON 无效。"); return 2;
    }
    inventoryInput.close();

    if (inventory.value("schemaVersion", 0) != 2) {
        WriteFailure(args, "本地更新清单协议版本不匹配。"); return 2;
    }

    auto const& files = inventory.value("files", json::array());
    if (files.empty() || files.size() > 10000) {
        WriteFailure(args, "更新文件清单为空或超出安全限制。"); return 2;
    }

    // Verify all files in stagingRoot against local inventory
    for (auto const& item : files) {
        auto relative = item.value("path", "");
        if (!IsSafeRelative(relative)) {
            WriteFailure(args, "更新清单包含不安全路径: " + relative); return 2;
        }
        auto source = ResolveUnder(args.stagingRoot, relative);
        if (!source || !fs::is_regular_file(*source)) {
            WriteFailure(args, "更新暂存文件缺失: " + relative); return 2;
        }
        std::error_code error;
        auto size = fs::file_size(*source, error);
        auto expectedSize = item.value("size", -1LL);
        if (error || size != static_cast<uintmax_t>(expectedSize)) {
            WriteFailure(args, "更新暂存文件大小不匹配: " + relative); return 2;
        }
        auto digest = Sha256File(*source);
        if (!digest || Hex(*digest) != item.value("sha256", "")) {
            WriteFailure(args, "更新暂存文件校验失败: " + relative); return 2;
        }
    }

    // Verify updater helper itself in staging if present
    auto updaterSource = ResolveUnder(args.stagingRoot, "UmamusumeAss.Updater.exe");
    if (updaterSource && fs::is_regular_file(*updaterSource)) {
        for (auto const& item : files) {
            if (item.value("path", "") == "UmamusumeAss.Updater.exe") {
                auto digest = Sha256File(*updaterSource);
                if (!digest || Hex(*digest) != item.value("sha256", "")) {
                    WriteFailure(args, "更新助手自身校验失败。"); return 2;
                }
                break;
            }
        }
    }

    if (!WaitForParent(args.parentPid)) {
        WriteFailure(args, "无法等待主程序退出。"); return 2;
    }

    fs::create_directories(args.backupRoot);
    std::vector<JournalEntry> journal;
    auto fail = [&](std::string const& reason) {
        Rollback(journal);
        WriteFailure(args, reason);
        return 2;
    };

    // Replace official resource directory as a whole (避免遗留旧资源)
    auto stagingResource = args.stagingRoot / "resource";
    if (fs::is_directory(stagingResource)) {
        auto targetResource = ResolveUnder(args.installRoot, "resource");
        auto backupResource = ResolveUnder(args.backupRoot, "resource");
        if (!targetResource || !backupResource) return fail("资源目录路径无效。");

        JournalEntry resourceEntry{ *targetResource, *backupResource, fs::exists(*targetResource), false, true };
        journal.push_back(resourceEntry);
        auto& recorded = journal.back();

        if (recorded.existed) {
            if (!MoveDirectory(recorded.target, recorded.backup)) return fail("备份官方资源目录失败。");
        }
        if (!MoveDirectory(stagingResource, recorded.target)) return fail("更新官方资源目录失败。");
        recorded.installed = true;
    }

    // Replace all other non-resource files individually
    for (auto const& item : files) {
        auto relative = item.value("path", "");
        if (relative.rfind("resource/", 0) == 0 || relative.rfind("resource\\", 0) == 0) {
            // Already replaced as part of whole resource directory
            continue;
        }
        auto source = ResolveUnder(args.stagingRoot, relative);
        auto target = ResolveUnder(args.installRoot, relative);
        auto backup = ResolveUnder(args.backupRoot, relative);
        if (!source || !target || !backup) return fail("更新清单包含非法路径: " + relative);

        JournalEntry entry{ *target, *backup, fs::exists(*target), false, false };
        journal.push_back(entry);
        auto& recorded = journal.back();

        if (recorded.existed && !MoveToBackup(recorded.target, recorded.backup)) {
            return fail("备份旧文件失败: " + relative);
        }
        if (!InstallAtomic(*source, recorded.target)) {
            return fail("安装更新文件失败: " + relative);
        }
        recorded.installed = true;
    }

    // Post-install verification
    for (auto const& item : files) {
        auto relative = item.value("path", "");
        auto target = ResolveUnder(args.installRoot, relative);
        if (!target || !fs::is_regular_file(*target)) {
            return fail("更新后文件缺失: " + relative);
        }
        std::error_code error;
        auto size = fs::file_size(*target, error);
        auto expectedSize = item.value("size", -1LL);
        if (error || size != static_cast<uintmax_t>(expectedSize)) {
            return fail("更新后文件大小不一致: " + relative);
        }
        auto digest = Sha256File(*target);
        if (!digest || Hex(*digest) != item.value("sha256", "")) {
            return fail("更新后文件哈希不一致: " + relative);
        }
    }

    WriteText(args.statusPath, "succeeded\n");

    // Clean up updates directory
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
