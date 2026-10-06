#include "launcher.h"

namespace dr {
bool modern_platform(DWORD major,DWORD,WORD architecture,DWORD build) { return major>=10 && (architecture==PROCESSOR_ARCHITECTURE_AMD64 || (architecture==12 && (major>10||build>=22000))); }
static uint64_t process_stamp(HANDLE process) { FILETIME created,ended,kernel,user;require(GetProcessTimes(process,&created,&ended,&kernel,&user)!=0,"Cannot identify launcher process.");return ((static_cast<uint64_t>(created.dwHighDateTime)<<32)|created.dwLowDateTime)+504911232000000000ULL; }
static Path stage_path(const Json& plan) {
    std::string name=plan.at("directory");require(name.size()==48&&name.find("launcher-update-")==0&&name.substr(16).find_first_not_of("0123456789abcdef")==std::string::npos,"Invalid update directory.");
    require(valid_hash(plan.at("previousHash"))&&valid_hash(plan.at("newHash"))&&plan.at("parentId").get<uint64_t>()>0&&plan.at("parentId").get<uint64_t>()<=MAXDWORD&&plan.at("parentStart").get<uint64_t>()>0,"Invalid update identity.");
    return under(absolute(wide(plan.at("root"))),".dr-client/"+name);
}
int update_worker(const Path& path) {
    try {
        Json plan=parse(read(path,8192));Path root=absolute(wide(plan.at("root"))),stage=stage_path(plan);
        require(!_wcsicmp(absolute(path).c_str(),under(stage,"plan.json").c_str())&&!_wcsicmp(self().c_str(),under(stage,"DungeonRunnersLauncher.exe").c_str())&&file_hash(self())==plan.at("newHash"),"The update worker does not match its download.");
        HANDLE parent=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_INFORMATION,FALSE,plan.at("parentId"));
        require(parent || GetLastError()==ERROR_INVALID_PARAMETER,"Cannot wait for the installed launcher. It was preserved.");
        if(parent) { try{require(process_stamp(parent)==plan.at("parentStart").get<uint64_t>(),"The parent process changed.");}catch(...){CloseHandle(parent);throw;} }
        atomic_write(under(stage,"ready"),{});
        if(parent) { DWORD result=WaitForSingleObject(parent,45000);CloseHandle(parent);require(result==WAIT_OBJECT_0,"The previous launcher did not exit."); }
        require(!exists(under(stage,"cancelled")),"The launcher update was cancelled.");
        Path lock_path=under(root,".dr-client/update.lock");HANDLE lock=CreateFileW(lock_path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);require(lock!=INVALID_HANDLE_VALUE,"Another update is running.");
        try {
            require(string(read(under(root,".dr-client/owner"),128))=="Dungeon-Runners-Launcher:1","Invalid installation owner.");
            Path target=under(root,"DungeonRunnersLauncher.exe"),marker=under(root,".dr-client/launcher.sha256"),backup=under(stage,"previous");std::string before=plan.at("previousHash"),after=plan.at("newHash");
            require(file_hash(target)==before&&string(read(marker,128))==before,"The installed launcher changed. It was preserved.");
            require(!exists(backup)&&CopyFileW(target.c_str(),backup.c_str(),TRUE)&&file_hash(backup)==before,"Cannot back up the launcher.");
            try {
                atomic_write(target,read(self(),268435456));require(file_hash(target)==after,"Updated launcher verification failed.");atomic_write(marker,bytes(after));
                atomic_write(under(root,".dr-client/launcher-update-result.json"),bytes(Json({{"success",true},{"message","Launcher updated successfully."}}).dump()));
                start(target,{},root,false);
            } catch(...) {
                if(exists(target)&&file_hash(target)==after){atomic_write(target,read(backup,268435456));atomic_write(marker,bytes(before));}throw;
            }
        } catch(...) {CloseHandle(lock);throw;}CloseHandle(lock);return 0;
    } catch(const std::exception& e) {
        try { Json plan=parse(read(path,8192));Path root=absolute(wide(plan.at("root")));stage_path(plan);atomic_write(under(root,".dr-client/launcher-update-result.json"),bytes(Json({{"success",false},{"message",std::string("Launcher update failed. ")+e.what()}}).dump())); }catch(...){}
        return 1;
    }
}
bool update_launcher(const Path& root,const Progress& progress) {
    Json release=parse(download("https://api.github.com/repos/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest",1048576));std::string tag=release.at("tag_name");require(!tag.empty()&&tag.size()<64&&tag.find_first_not_of("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-_")==std::string::npos&&!release.at("draft").get<bool>()&&!release.at("prerelease").get<bool>(),"Invalid launcher release.");
    Json asset;for(const auto& candidate:release.at("assets"))if(candidate.at("name")=="DungeonRunnersLauncher.exe"){require(asset.is_null(),"Duplicate launcher download.");asset=candidate;}
    require(!asset.is_null(),"The Windows installer is missing from the release.");std::string url=asset.at("browser_download_url"),digest=asset.at("digest");uint64_t size=asset.at("size");
    require(size>0&&size<=268435456&&url=="https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/download/"+tag+"/DungeonRunnersLauncher.exe"&&digest.find("sha256:")==0&&valid_hash(digest.substr(7)),"The launcher release has no valid download checksum.");
    std::string sha=digest.substr(7);Path target=under(root,"DungeonRunnersLauncher.exe");if(exists(target)&&file_hash(target)==sha)return false;
    require(exists(target)&&string(read(under(root,".dr-client/launcher.sha256"),128))==file_hash(target),"The installed launcher was modified. It was preserved.");
    GUID id;require(SUCCEEDED(CoCreateGuid(&id)),"Cannot create update transaction.");std::string name="launcher-update-";const auto* raw=reinterpret_cast<const unsigned char*>(&id);const char* hex="0123456789abcdef";for(size_t i=0;i<sizeof id;i++){name+=hex[raw[i]>>4];name+=hex[raw[i]&15];}
    Path stage=under(root,".dr-client/"+name);folders(stage);Path helper=under(stage,"DungeonRunnersLauncher.exe");download(url,helper,size,progress);require(length(helper)==size&&file_hash(helper)==sha,"Launcher download verification failed.");
    HMODULE candidate=LoadLibraryExW(helper.c_str(),nullptr,LOAD_LIBRARY_AS_DATAFILE);bool universal=false;
    if(candidate) {
        HRSRC marker=FindResourceW(candidate,MAKEINTRESOURCEW(105),RT_RCDATA);
        const char identity[]="Dungeon-Runners-Windows-Bundle:1";
        if(marker && SizeofResource(candidate,marker)==sizeof(identity)-1) {
            void* data=LockResource(LoadResource(candidate,marker));universal=data && memcmp(data,identity,sizeof(identity)-1)==0;
        }
        FreeLibrary(candidate);
    }
    if(!universal)return false;
    Json plan={{"root",utf8(root)},{"directory",name},{"previousHash",file_hash(target)},{"newHash",sha},{"parentId",GetCurrentProcessId()},{"parentStart",process_stamp(GetCurrentProcess())}};Path plan_path=under(stage,"plan.json");atomic_write(plan_path,bytes(plan.dump()));
    start(helper,{L"--apply-launcher-update",plan_path},stage,false);
    for(int i=0;i<150;i++){if(exists(under(stage,"ready")))return true;Sleep(100);}atomic_write(under(stage,"cancelled"),{});throw std::runtime_error("The update could not start. Your installed launcher was preserved.");
}
int dispatch(const std::vector<Path>& args) {
    if(args.size()==2&&args[0]==L"--preview-ui")return legacy_ui(true,absolute(args[1]));
    if(args.size()==3&&args[0]==L"--verify-addons"){verify_addon_package(game_root(args[1]),args[2]);return 0;}
    if(args.size()==2&&args[0]==L"--apply-launcher-update")return update_worker(args[1]);
    if(args.size()==2&&args[0]==L"--self-test")return run_tests(args[1]);
    if(args.size()==2&&args[0]==L"--test-https")return test_https(args[1]);
    if(args.size()==2&&args[0]==L"--verify-feed") { Json data=manifest(download(feed,131072));write(args[1],bytes(Json({{"version",data.at("version")},{"signatureVerified",true},{"tlsMinimum","1.2"},{"packages",data.at("packages").size()}}).dump(2)));return 0; }
    if(args.size()==2&&args[0]==L"--licenses") {write(args[1],resource(103));return 0;}
    OSVERSIONINFOW os{};os.dwOSVersionInfoSize=sizeof os;require(GetVersionExW(&os)!=0,"Cannot detect Windows version.");SYSTEM_INFO system{};GetNativeSystemInfo(&system);
    bool legacy=std::find(args.begin(),args.end(),L"--legacy")!=args.end(),smoke=std::find(args.begin(),args.end(),L"--smoke-test")!=args.end();
    if(args.size()==2&&args[0]==L"--detect") {write(args[1],bytes(Json({{"windowsMajor",os.dwMajorVersion},{"windowsMinor",os.dwMinorVersion},{"windowsBuild",os.dwBuildNumber},{"architecture",system.wProcessorArchitecture},{"mode",modern_platform(os.dwMajorVersion,os.dwMinorVersion,system.wProcessorArchitecture,os.dwBuildNumber)?"modern":"legacy"}}).dump(2)));return 0;}
    if(legacy||!modern_platform(os.dwMajorVersion,os.dwMinorVersion,system.wProcessorArchitecture,os.dwBuildNumber))return legacy_ui(smoke);
    Bytes modern=resource(100);std::string digest=hash(modern);Path cache=under(local_data(),"Dungeon Runners Launcher/runtime/"+digest);folders(cache);Path executable=under(cache,"DungeonRunnersLauncher.exe");
    if(!exists(executable)||file_hash(executable)!=digest)atomic_write(executable,modern);
    std::vector<Path> forwarded{L"--windows-bundle",self(),std::to_wstring(GetCurrentProcessId())};forwarded.insert(forwarded.end(),args.begin(),args.end());
    return static_cast<int>(start(executable,forwarded,directory(self()),true));
}
}

int WINAPI wWinMain(HINSTANCE,HINSTANCE,LPWSTR,int) {
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    WSADATA winsock{};if(WSAStartup(MAKEWORD(2,2),&winsock)!=0)return 1;
    int count=0;LPWSTR* raw=CommandLineToArgvW(GetCommandLineW(),&count);std::vector<dr::Path> args;for(int i=1;i<count;i++)args.push_back(raw[i]);LocalFree(raw);
    int result=1;try{result=dr::dispatch(args);}catch(const std::exception& error){
        if(args.size()==2&&(args[0]==L"--self-test"||args[0]==L"--verify-feed"||args[0]==L"--detect"||args[0]==L"--test-https")) {try{dr::write(args[1],dr::bytes(dr::Json({{"error",error.what()}}).dump(2)));}catch(...){}}
        else if(args.size()==3&&args[0]==L"--verify-addons") {try{dr::write(args[2],dr::bytes(dr::Json({{"error",error.what()}}).dump(2)));}catch(...){}}
        else if(args.size()==2&&args[0]==L"--preview-ui") {try{dr::folders(dr::absolute(args[1]));dr::write(dr::under(dr::absolute(args[1]),"error.json"),dr::bytes(dr::Json({{"error",error.what()}}).dump(2)));}catch(...){}}
        else MessageBoxW(nullptr,dr::wide(error.what()).c_str(),L"Dungeon Runners Launcher",MB_OK|MB_ICONERROR);
    }WSACleanup();return result;
}
