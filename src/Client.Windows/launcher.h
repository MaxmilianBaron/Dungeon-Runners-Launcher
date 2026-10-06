#pragma once
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <tlhelp32.h>
#include <wincrypt.h>
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <functional>
#include <map>
#include <memory>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>
#include "json.hpp"
#include "mbedtls/sha256.h"
#include "mbedtls/pk.h"
#include "mbedtls/base64.h"
#include "mbedtls/ecdsa.h"
#include "mbedtls/ssl.h"
#include "mbedtls/entropy.h"
#include "mbedtls/ctr_drbg.h"
#include "mbedtls/error.h"
#include "miniz.h"

namespace dr {
using Json = nlohmann::json;
using Bytes = std::vector<unsigned char>;
using Path = std::wstring;
using Progress = std::function<void(const std::string&, uint64_t, uint64_t)>;
extern volatile LONG cancelled;
extern const char* feed;
extern const char* public_key;
void require(bool valid, const std::string& message);
void check_cancelled();
std::string utf8(const Path& text);
Path wide(const std::string& text);
Path self();
Path directory(const Path& path);
Path absolute(const Path& path);
Path game_root(const Path& path);
void no_links(const Path& path);
Path under(const Path& root, const std::string& relative);
bool exists(const Path& path);
void folders(const Path& path);
uint64_t length(const Path& path);
Bytes read(const Path& path, uint64_t limit);
void write(const Path& path, const Bytes& data);
void atomic_write(const Path& path, const Bytes& data);
Bytes bytes(const std::string& value);
std::string string(const Bytes& value);
std::string hex(const unsigned char* data, size_t size);
std::string hash(const void* data, size_t size);
std::string hash(const Bytes& data);
std::string file_hash(const Path& path);
bool valid_hash(const std::string& value);
Bytes resource(int id);
Json parse(const Bytes& data);
Path local_data();
std::wstring quote(const Path& argument);
DWORD start(const Path& executable, const std::vector<Path>& args, const Path& cwd, bool wait, bool elevate = false);
void game_closed();
void download(const std::string& url, const Path& target, uint64_t limit, const Progress& progress = {});
Bytes download(const std::string& url, uint64_t limit);
bool allowed_url(const std::string& url);
Json manifest(const Bytes& signed_bytes);
bool compatible(const Bytes& image, const Json& profile);
Bytes prepare_client(const Bytes& image);
void recover(const Path& root);
void installation_lock(const Path& root, const std::function<void()>& action);
void recover_addons(const Path& root);
void install_addons(const Path& root, const Progress& progress);
void remove_addons(const Path& root, const Progress& progress);
Bytes addon_skin(const Path& root, const Bytes& metadata);
void addon_contracts(const Path& root);
void verify_addon_package(const Path& root, const Path& result);
void install(const Path& root, const Bytes& signed_bytes, const Progress& progress);
void play(const Path& root, const Progress& progress);
void requirements(const Path& root, const Progress& progress);
void entry_points(const Path& root);
int update_worker(const Path& plan);
bool update_launcher(const Path& root, const Progress& progress);
int legacy_ui(bool smoke, const Path& previews = L"");
bool modern_platform(DWORD major, DWORD minor, WORD architecture, DWORD build = 0);
int dispatch(const std::vector<Path>& args);
int run_tests(const Path& result);
int test_https(const Path& result);
void installation_contracts(const Path& root);
}
