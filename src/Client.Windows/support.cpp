#include "launcher.h"

extern "C" int mbedtls_hardware_poll(void*, unsigned char* output, size_t len, size_t* olen) {
    HCRYPTPROV provider = 0;
    *olen = 0;
    if (len > MAXDWORD || !CryptAcquireContextW(&provider, nullptr, nullptr, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT | CRYPT_SILENT)) return MBEDTLS_ERR_ENTROPY_SOURCE_FAILED;
    BOOL ok = CryptGenRandom(provider, static_cast<DWORD>(len), output);
    CryptReleaseContext(provider, 0);
    if (!ok) return MBEDTLS_ERR_ENTROPY_SOURCE_FAILED;
    *olen = len;
    return 0;
}

namespace dr {
volatile LONG cancelled = 0;
const char* feed = "https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest/download/client-manifest.json";
const char* public_key = "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEK1P0wmYpLnaSqy2n3ZpSizpThY+7\nNMtp63AHWIyaOygW925LUKp5R565fG01fi2ucWsAa+ZS/nfrH7kGHbSXfw==\n-----END PUBLIC KEY-----\n";

void require(bool valid, const std::string& message) { if (!valid) throw std::runtime_error(message); }
void check_cancelled() { require(InterlockedCompareExchange(&cancelled, 0, 0) == 0, "Cancelled."); }
std::string utf8(const Path& text) {
    int size = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    require(size > 0 || text.empty(), "Invalid text.");
    std::string result(size, 0);
    if (size) WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), &result[0], size, nullptr, nullptr);
    return result;
}
Path wide(const std::string& text) {
    int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
    require(size > 0 || text.empty(), "Invalid UTF-8 text.");
    Path result(size, 0);
    if (size) MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), &result[0], size);
    return result;
}
Path self() { wchar_t path[32768]; DWORD n = GetModuleFileNameW(nullptr, path, 32768); require(n > 0 && n < 32768, "Cannot locate launcher."); return Path(path, n); }
Path directory(const Path& path) { auto end = path.find_last_of(L"\\/"); require(end != Path::npos, "Invalid directory."); return path.substr(0, end); }
Path absolute(const Path& path) {
    wchar_t result[32768]; DWORD n = GetFullPathNameW(path.c_str(), 32768, result, nullptr);
    require(n >= 3 && n < MAX_PATH - 16 && result[1] == L':' && result[2] == L'\\', "Select a local game folder with a shorter path.");
    Path value(result, n); while (value.size() > 3 && value.back() == L'\\') value.pop_back();
    return value;
}
Path game_root(const Path& path) {
    Path value=absolute(path);
    require(value.size()>3,"Choose a dedicated game folder.");
    wchar_t system[MAX_PATH];
    require(GetWindowsDirectoryW(system,MAX_PATH)>0,"Cannot locate the Windows folder.");
    Path windows=absolute(system);
    require(_wcsicmp(value.c_str(),windows.c_str())!=0 && !(value.size()>windows.size() && value[windows.size()]==L'\\' && _wcsnicmp(value.c_str(),windows.c_str(),windows.size())==0),"The Windows folder cannot be used for the game.");
    for(int id:{CSIDL_PROFILE,CSIDL_DESKTOPDIRECTORY,CSIDL_PERSONAL,CSIDL_LOCAL_APPDATA,CSIDL_PROGRAM_FILES}) {
        if(SUCCEEDED(SHGetFolderPathW(nullptr,id,nullptr,SHGFP_TYPE_CURRENT,system)))
            require(_wcsicmp(value.c_str(),absolute(system).c_str())!=0,"Choose a dedicated game folder inside this location.");
    }
    no_links(value);return value;
}
void no_links(const Path& path) {
    Path value = absolute(path);
    for (size_t i = 3; i <= value.size(); i++) if (i == value.size() || value[i] == L'\\') {
        DWORD a = GetFileAttributesW(value.substr(0, i).c_str());
        require(a == INVALID_FILE_ATTRIBUTES || !(a & FILE_ATTRIBUTE_REPARSE_POINT), "Linked installation paths are not supported.");
    }
}
Path under(const Path& root, const std::string& relative) {
    require(!relative.empty() && relative.size() < 180 && relative.find_first_of(":\\\r\n") == std::string::npos && relative.find('\0') == std::string::npos && relative.front() != '/' && relative.back() != '/', "Invalid file path.");
    std::istringstream stream(relative); std::string component;
    while (std::getline(stream, component, '/')) {
        require(!component.empty() && component != "." && component != ".." && component.back() != '.' && component.back() != ' ' && component.find_first_of("<>\"|?*") == std::string::npos, "Invalid file path.");
        std::string stem = component.substr(0, component.find('.'));
        std::transform(stem.begin(), stem.end(), stem.begin(), [](unsigned char c) { return static_cast<char>(toupper(c)); });
        require(stem != "CON" && stem != "PRN" && stem != "AUX" && stem != "NUL" && !(stem.size() == 4 && (stem.substr(0,3) == "COM" || stem.substr(0,3) == "LPT") && isdigit(stem[3])), "Reserved file name.");
    }
    Path part = wide(relative); std::replace(part.begin(), part.end(), L'/', L'\\');
    Path result = absolute(root + L"\\" + part); no_links(result); return result;
}
bool exists(const Path& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; }
void folders(const Path& path) {
    no_links(path); if (exists(path)) { require(GetFileAttributesW(path.c_str()) & FILE_ATTRIBUTE_DIRECTORY, "A file blocks an installation folder."); return; }
    if (path.size() > 3) folders(directory(path));
    require(CreateDirectoryW(path.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS, "Cannot create installation folder.");
}
uint64_t length(const Path& path) {
    WIN32_FILE_ATTRIBUTE_DATA data{};
    require(GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data) && !(data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY), "Cannot read file.");
    return (static_cast<uint64_t>(data.nFileSizeHigh) << 32) | data.nFileSizeLow;
}
struct File {
    HANDLE handle;
    File(const Path& path, DWORD access, DWORD disposition) : handle(CreateFileW(path.c_str(), access, access == GENERIC_READ ? FILE_SHARE_READ : 0, nullptr, disposition, FILE_ATTRIBUTE_NORMAL, nullptr)) { require(handle != INVALID_HANDLE_VALUE, "Cannot open file: " + utf8(path)); }
    ~File() { CloseHandle(handle); }
};
Bytes read(const Path& path, uint64_t limit) {
    no_links(path); uint64_t size = length(path); require(size <= limit && size <= SIZE_MAX, "File is too large.");
    File file(path, GENERIC_READ, OPEN_EXISTING); Bytes data(static_cast<size_t>(size)); DWORD got = 0;
    require(size == 0 || (ReadFile(file.handle, data.data(), static_cast<DWORD>(size), &got, nullptr) && got == size), "Could not read complete file."); return data;
}
void write(const Path& path, const Bytes& data) {
    no_links(path); folders(directory(path)); File file(path, GENERIC_WRITE, CREATE_ALWAYS); DWORD wrote = 0;
    require(data.size() <= MAXDWORD && (data.empty() || (WriteFile(file.handle, data.data(), static_cast<DWORD>(data.size()), &wrote, nullptr) && wrote == data.size())) && FlushFileBuffers(file.handle), "Could not save complete file.");
}
void atomic_write(const Path& path, const Bytes& data) {
    no_links(path);folders(directory(path));
    GUID id;require(SUCCEEDED(CoCreateGuid(&id)),"Cannot create temporary file identity.");
    Path temporary=under(directory(path),"replace-"+hex(reinterpret_cast<unsigned char*>(&id),sizeof id)+".tmp");
    bool created=false;
    try {
        File file(temporary,GENERIC_WRITE,CREATE_NEW);created=true;DWORD wrote=0;
        require(data.size()<=MAXDWORD && (data.empty() || (WriteFile(file.handle,data.data(),static_cast<DWORD>(data.size()),&wrote,nullptr) && wrote==data.size())) && FlushFileBuffers(file.handle),"Could not save replacement file.");
    } catch(...) { if(created)DeleteFileW(temporary.c_str());throw; }
    bool moved=MoveFileExW(temporary.c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=0;
    if(!moved)DeleteFileW(temporary.c_str());
    require(moved,"Could not replace file. Close the game and retry.");
}
Bytes bytes(const std::string& value) { return Bytes(value.begin(), value.end()); }
std::string string(const Bytes& value) { return std::string(value.begin(), value.end()); }
std::string hex(const unsigned char* data, size_t size) { const char* digits = "0123456789abcdef"; std::string out; for (size_t i=0;i<size;i++) { out += digits[data[i]>>4]; out += digits[data[i]&15]; } return out; }
std::string hash(const void* data, size_t size) { unsigned char digest[32]; require(mbedtls_sha256(static_cast<const unsigned char*>(data), size, digest, 0) == 0, "SHA-256 failed."); return hex(digest, 32); }
std::string hash(const Bytes& data) { return hash(data.data(), data.size()); }
bool valid_hash(const std::string& value) { return value.size() == 64 && value.find_first_not_of("0123456789abcdef") == std::string::npos; }
std::string file_hash(const Path& path) {
    no_links(path); File file(path, GENERIC_READ, OPEN_EXISTING);
    mbedtls_sha256_context ctx; mbedtls_sha256_init(&ctx); mbedtls_sha256_starts(&ctx, 0);
    unsigned char buffer[65536], digest[32]; DWORD count;
    while (true) { require(ReadFile(file.handle, buffer, sizeof buffer, &count, nullptr), "File verification failed."); if (!count) break; require(mbedtls_sha256_update(&ctx, buffer, count) == 0, "SHA-256 failed."); }
    require(mbedtls_sha256_finish(&ctx, digest) == 0, "SHA-256 failed."); mbedtls_sha256_free(&ctx); return hex(digest, 32);
}
Bytes resource(int id) { auto r=FindResourceW(nullptr, MAKEINTRESOURCEW(id), RT_RCDATA); require(r != nullptr, "Installer component is missing."); auto h=LoadResource(nullptr,r); auto p=static_cast<unsigned char*>(LockResource(h)); require(p != nullptr, "Cannot read installer component."); return Bytes(p,p+SizeofResource(nullptr,r)); }
Json parse(const Bytes& data) {
    std::map<int,std::set<std::string>> keys;
    return Json::parse(data.begin(),data.end(),[&](int depth,Json::parse_event_t event,Json& value) {
        require(depth<=32,"JSON nesting is too deep.");
        if(event==Json::parse_event_t::object_start)keys[depth+1].clear();
        if(event==Json::parse_event_t::key)require(keys[depth].insert(value.get<std::string>()).second,"Duplicate JSON field.");
        return true;
    });
}
Path local_data() { wchar_t path[MAX_PATH]; require(SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA | CSIDL_FLAG_CREATE, nullptr, SHGFP_TYPE_CURRENT, path)), "Cannot locate application data."); return path; }
Path quote(const Path& value) {
    Path out=L"\""; size_t slashes=0;
    for (wchar_t c:value) { if (c==L'\\') { slashes++; continue; } if(c==L'\"') out.append(slashes*2+1,L'\\'); else out.append(slashes,L'\\'); slashes=0; out+=c; }
    out.append(slashes*2,L'\\'); return out+L'\"';
}
DWORD start(const Path& executable, const std::vector<Path>& args, const Path& cwd, bool wait, bool elevate) {
    Path parameters; for (const auto& arg:args) { if (!parameters.empty()) parameters+=L' '; parameters+=quote(arg); }
    HANDLE process=nullptr;
    if (elevate) {
        SHELLEXECUTEINFOW info{}; info.cbSize=sizeof info; info.fMask=SEE_MASK_NOCLOSEPROCESS; info.lpVerb=L"runas"; info.lpFile=executable.c_str(); info.lpParameters=parameters.c_str(); info.lpDirectory=cwd.c_str(); info.nShow=SW_HIDE;
        require(ShellExecuteExW(&info) && info.hProcess, "Requirements setup was cancelled or could not start."); process=info.hProcess;
    } else {
        Path command=quote(executable)+(parameters.empty()?L"":L" "+parameters); STARTUPINFOW si{}; si.cb=sizeof si; PROCESS_INFORMATION pi{};
        require(CreateProcessW(executable.c_str(), &command[0], nullptr,nullptr,FALSE,0,nullptr,cwd.c_str(),&si,&pi),"Could not start application."); CloseHandle(pi.hThread); process=pi.hProcess;
    }
    DWORD code=0; if (wait) { WaitForSingleObject(process,INFINITE); GetExitCodeProcess(process,&code); } CloseHandle(process); return code;
}
void game_closed() {
    HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0); require(snapshot!=INVALID_HANDLE_VALUE,"Could not check running games.");
    PROCESSENTRY32W p{}; p.dwSize=sizeof p; bool running=false;
    if(Process32FirstW(snapshot,&p)) do { if(!_wcsicmp(p.szExeFile,L"DungeonRunners.exe")) running=true; } while(Process32NextW(snapshot,&p));
    CloseHandle(snapshot); require(!running,"Close Dungeon Runners before changing the installation.");
}
}
