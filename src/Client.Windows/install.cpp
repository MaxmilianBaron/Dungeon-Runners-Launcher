#include "launcher.h"

namespace dr {
static const std::set<std::string> managed={"DungeonRunners.exe","dbghelp.dll","fmodex.dll","game.pkg","game.pki","config/default.cfg","config/resourcemanager.cfg","config/DungeonRunners.cfg","config/User.cfg"};
static bool seed(const std::string& path) { return path=="config/DungeonRunners.cfg"||path=="config/User.cfg"; }
static Bytes base64(const std::string& data) { require(data.size()<175000,"Encoded data is too large."); Bytes out(data.size()); size_t len=0; require(mbedtls_base64_decode(out.data(),out.size(),&len,reinterpret_cast<const unsigned char*>(data.data()),data.size())==0,"Invalid encoded manifest."); out.resize(len); return out; }
static Bytes der_signature(const Bytes& signature) {
    require(signature.size()==64,"Invalid update signature."); Bytes out{0x30,0};
    for(size_t start:{size_t(0),size_t(32)}) { size_t end=start+32; while(start+1<end && signature[start]==0) start++; bool pad=(signature[start]&128)!=0; out.push_back(2); out.push_back(static_cast<unsigned char>(end-start+(pad?1:0))); if(pad)out.push_back(0); out.insert(out.end(),signature.begin()+start,signature.begin()+end); }
    out[1]=static_cast<unsigned char>(out.size()-2); return out;
}
static std::vector<unsigned> version(const std::string& text) {
    require(!text.empty() && text.size()<25 && text.back()!='.',"Invalid release version."); std::vector<unsigned> values; std::istringstream parts(text); std::string part;
    while(std::getline(parts,part,'.')) { require(!part.empty()&&part.size()<6&&part.find_first_not_of("0123456789")==std::string::npos,"Invalid release version."); values.push_back(static_cast<unsigned>(std::stoul(part))); }
    require(values.size()>=2&&values.size()<=4&&values[0]>=1,"Invalid release version."); values.resize(4); return values;
}
Json manifest(const Bytes& signed_bytes) {
    require(signed_bytes.size()<=131072,"Manifest is too large."); Json envelope=parse(signed_bytes); require(envelope.size()==2,"Invalid signed manifest envelope.");
    Bytes payload=base64(envelope.at("payload").get<std::string>()),signature=der_signature(base64(envelope.at("signature").get<std::string>()));
    mbedtls_pk_context key; mbedtls_pk_init(&key); int result=mbedtls_pk_parse_public_key(&key,reinterpret_cast<const unsigned char*>(public_key),strlen(public_key)+1);
    unsigned char digest[32]; mbedtls_sha256(payload.data(),payload.size(),digest,0); if(result==0) result=mbedtls_pk_verify(&key,MBEDTLS_MD_SHA256,digest,sizeof digest,signature.data(),signature.size()); mbedtls_pk_free(&key);
    require(result==0,"The update signature is invalid."); Json data=parse(payload);
    require(data.is_object()&&data.size()==3,"Unsupported manifest fields.");
    require(data.at("schema")==1||data.at("schema")==2,"Unsupported manifest schema."); version(data.at("version").get<std::string>());
    const auto& packages=data.at("packages"); require(packages.is_array()&&packages.size()>0&&packages.size()<=8,"Invalid package list."); std::set<std::string> names,paths; uint64_t total=0;
    for(const auto& package:packages) {
        require(package.is_object()&&package.size()==5,"Unsupported package fields.");
        std::string name=package.at("name"),url=package.at("url"),sha=package.at("sha256"); uint64_t size=package.at("size");
        require((name=="Game-Client.zip"||name=="Game-Data.zip")&&names.insert(name).second&&size>0&&size<=2147483648ULL&&valid_hash(sha),"Invalid package identity.");
        require(url.find("https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/")==0&&allowed_url(url),"Untrusted package location.");
        const auto& files=package.at("files"); require(files.is_array()&&files.size()>0&&files.size()<=16,"Invalid file list.");
        for(const auto& file:files) { require(file.is_object()&&file.size()==4,"Unsupported file fields.");std::string path=file.at("path"); uint64_t bytes=file.at("size"); require(managed.count(path)&&paths.insert(path).second&&bytes>0&&bytes<=2147483648ULL&&valid_hash(file.at("sha256"))&&file.at("seed")==seed(path),"Invalid managed file identity."); total+=bytes; }
    }
    require(paths==managed&&total<=4294967296ULL,"Incomplete client manifest."); return data;
}
bool compatible(const Bytes& image,const Json& profile) {
    uint64_t min=profile.at("minimumSize"),max=profile.at("maximumSize"); if(image.size()<min||image.size()>max) return false;
    uint64_t end=0; const auto& ranges=profile.at("ranges"); require(profile.at("schema")==1&&max<=33554432&&ranges.is_array()&&ranges.size()>0&&ranges.size()<=512,"Invalid compatibility profile.");
    for(const auto& range:ranges) {
        uint64_t offset=range.at("offset"),size=range.at("length"); require(offset>=end&&size>0&&size<=min&&offset<=min-size,"Invalid protected range."); end=offset+size;
        auto expected=range.at("sha256").get<std::vector<std::string>>(); require(!expected.empty()&&expected.size()<=4,"Invalid protected range hashes.");
        if(std::find(expected.begin(),expected.end(),hash(image.data()+offset,static_cast<size_t>(size)))==expected.end()) return false;
    }
    return true;
}
Bytes prepare_client(const Bytes& image) {
    const auto profiles=parse(resource(102)); if(compatible(image,profiles.at("installed"))) return image;
    require(compatible(image,profiles.at("legacy")),"The game executable contains incompatible changes. It was preserved.");
    Bytes out=image; auto set=[&](size_t offset,uint32_t value) { for(size_t i=0;i<4;i++)out.at(offset+i)=static_cast<unsigned char>(value>>(8*i)); };
    set(0x278,0xD79FD); set(0x2FED11,0x9029E0); out.at(0x2FED17)=28; std::fill(out.begin()+0x4C95B0,out.begin()+0x4C95C8,0);
    const char name[]=".\\DungeonRunnersLauncher.exe"; std::copy(name,name+sizeof name,out.begin()+0x5019E0); require(compatible(out,profiles.at("installed")),"Client entry-point verification failed."); return out;
}
class Lock {
    HANDLE file=INVALID_HANDLE_VALUE;
public:
    explicit Lock(const Path& root) {
        Path work=under(root,".dr-client"),owner=under(work,"owner");
        if(exists(work)) require(exists(owner)&&string(read(owner,128))=="Dungeon-Runners-Launcher:1","The launcher data folder has an unknown owner.");
        folders(work); if(!exists(owner))atomic_write(owner,bytes("Dungeon-Runners-Launcher:1"));
        file=CreateFileW(under(work,"update.lock").c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
        require(file!=INVALID_HANDLE_VALUE,"Another launcher is working on this installation.");
    }
    ~Lock(){if(file!=INVALID_HANDLE_VALUE)CloseHandle(file);}
};
void installation_lock(const Path& root,const std::function<void()>& action) { Lock lock(root); action(); }
static void remove_tree(const Path& root) {
    no_links(root); WIN32_FIND_DATAW data{}; HANDLE find=FindFirstFileW((root+L"\\*").c_str(),&data);
    if(find!=INVALID_HANDLE_VALUE) { do { if(!wcscmp(data.cFileName,L".")||!wcscmp(data.cFileName,L".."))continue; Path item=under(root,utf8(data.cFileName)); if(data.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)remove_tree(item); else require(DeleteFileW(item.c_str())!=0,"Cannot remove staging file."); }while(FindNextFileW(find,&data)); FindClose(find); }
    require(RemoveDirectoryW(root.c_str())||GetLastError()==ERROR_PATH_NOT_FOUND,"Cannot remove staging directory.");
}
void recover(const Path& root) {
    Path tx=under(root,".dr-client/transaction"),path=under(tx,"journal.json"); if(!exists(tx))return;
    if(exists(path)) {
        Json journal=parse(read(path,65536)); const auto& files=journal.at("files"); require(files.is_array()&&files.size()<=16,"Invalid recovery journal."); std::set<std::string> seen;
        for(const auto& file:files) { std::string name=file.at("path"); require(managed.count(name)&&seen.insert(name).second&&valid_hash(file.at("newHash"))&&(!file.at("existed").get<bool>()||valid_hash(file.at("previousHash"))),"Invalid recovery file."); }
        if(!journal.at("committed").get<bool>()) for(auto it=files.rbegin();it!=files.rend();++it) {
            const auto& file=*it; std::string name=file.at("path"); Path target=under(root,name),source=under(tx,"new/"+name),backup=under(tx,"previous/"+name); bool existed=file.at("existed");
            if(exists(backup)) { require(existed&&file_hash(backup)==file.at("previousHash"),"Recovery backup verification failed."); if(exists(target)) { require(file_hash(target)==file.at("newHash"),"A file was edited after the interrupted update. It was preserved."); require(DeleteFileW(target.c_str())!=0,"Cannot restore game file."); } folders(directory(target)); require(MoveFileW(backup.c_str(),target.c_str())!=0,"Cannot restore game backup."); }
            else if(!existed&&!exists(source)&&exists(target)) { require(file_hash(target)==file.at("newHash"),"A new file was edited after the interrupted update. It was preserved."); require(DeleteFileW(target.c_str())!=0,"Cannot restore installation."); }
            else if(existed) require(exists(target)&&file_hash(target)==file.at("previousHash"),"An original file is missing. Recovery data was preserved.");
        }
    }
    remove_tree(tx);
}
static Bytes connection_config(const Bytes& input) {
    require(input.size()<=65536,"Connection configuration is too large."); std::string text=input.empty()?"[ResourceManager]\r\nResourceConfig = ResourceManager.cfg\r\n":string(input); if(text.compare(0,3,"\xef\xbb\xbf")==0)text.erase(0,3);
    std::string normalized; for(size_t i=0;i<text.size();i++) { if(text[i]=='\r') { normalized+='\n'; if(i+1<text.size()&&text[i+1]=='\n')i++; } else normalized+=text[i]; }
    std::istringstream lines(normalized); std::string line,result; bool auth=false,found=false;
    while(std::getline(lines,line)) { auto trim=line; auto begin=trim.find_first_not_of(" \t"); trim=begin==std::string::npos?"":trim.substr(begin); auto end=trim.find_last_not_of(" \t"); if(end!=std::string::npos)trim.resize(end+1); std::transform(trim.begin(),trim.end(),trim.begin(),[](unsigned char c){return static_cast<char>(tolower(c));});
        if(trim.size()>=2&&trim.front()=='['&&trim.back()==']') { auth=trim=="[authserver]"; if(auth) { result+="[AuthServer]\r\nAddress = play.dungeonrunnersreborn.com\r\nPort = 2110\r\n";found=true;continue; } }
        if(auth) { auto equal=trim.find('='); if(equal!=std::string::npos) { auto key=trim.substr(0,equal); while(!key.empty()&&(key.back()==' '||key.back()=='\t'))key.pop_back(); if(key=="address"||key=="port")continue; } } result+=line+"\r\n";
    }
    if(!found)result="[AuthServer]\r\nAddress = play.dungeonrunnersreborn.com\r\nPort = 2110\r\n"+result; return bytes(result);
}
static void runtime_folders(const Path& root) { for(const char* name:{"logs","DFData/cache/data/BlankBanner","DFData/cache/data/DF256x256","DFData/cache/data/juicy320_320","DFData/cache/data/TRad300x250","DFData/cache/data/TRad512x512","DFData/cache/data/TRad728x90"})folders(under(root,name)); }
static void stage(const Json& package,const Path& archive,const Path& root) {
    require(length(archive)==package.at("size").get<uint64_t>()&&file_hash(archive)==package.at("sha256"),"Downloaded package failed verification.");
    FILE* stream=nullptr; _wfopen_s(&stream,archive.c_str(),L"rb"); require(stream!=nullptr,"Cannot open package."); mz_zip_archive zip{};
    if(!mz_zip_reader_init_cfile(&zip,stream,0,0)) { fclose(stream); throw std::runtime_error("Invalid ZIP package."); }
    try {
        std::map<std::string,Json> expected; for(const auto& file:package.at("files"))expected.emplace(file.at("path"),file);
        require(mz_zip_reader_get_num_files(&zip)==expected.size(),"Unexpected archive entries."); std::set<std::string> seen;
        for(mz_uint i=0;i<mz_zip_reader_get_num_files(&zip);i++) { mz_zip_archive_file_stat info{}; require(mz_zip_reader_file_stat(&zip,i,&info)!=0,"Invalid archive entry.");
            std::string name=info.m_filename; require(expected.count(name)&&seen.insert(name).second&&!info.m_is_directory&&!info.m_is_encrypted&&info.m_uncomp_size==expected.at(name).at("size").get<uint64_t>()&&((info.m_external_attr>>16)&0xf000)!=0xa000&&!(info.m_external_attr&0x400),"Invalid, linked or duplicate archive entry."); under(root,name);
        }
        for(mz_uint i=0;i<mz_zip_reader_get_num_files(&zip);i++) { check_cancelled(); mz_zip_archive_file_stat info{}; mz_zip_reader_file_stat(&zip,i,&info); Path output=under(root,info.m_filename); folders(directory(output)); FILE* file=nullptr; _wfopen_s(&file,output.c_str(),L"wb"); require(file!=nullptr,"Cannot extract game file."); bool ok=mz_zip_reader_extract_to_cfile(&zip,i,file,0)!=0; fclose(file); require(ok&&length(output)==info.m_uncomp_size&&file_hash(output)==expected.at(info.m_filename).at("sha256"),"Extracted file verification failed."); }
    } catch(...) { mz_zip_reader_end(&zip); fclose(stream); throw; } mz_zip_reader_end(&zip); fclose(stream);
}
void install(const Path& root,const Bytes& signed_bytes,const Progress& progress) {
    Json data=manifest(signed_bytes); game_closed(); folders(root); Lock lock(root); recover(root); recover_addons(root);
    Path work=under(root,".dr-client"),saved=under(work,"manifest.json");
    if(exists(saved))require(version(data.at("version"))>=version(manifest(read(saved,131072)).at("version")),"An older release cannot replace the installed game.");
    std::set<std::string> pending; Bytes existing_image,updated_image;
    Path executable=under(root,"DungeonRunners.exe"); if(exists(executable)) { existing_image=read(executable,33554432); updated_image=prepare_client(existing_image); }
    uint64_t needed=32*1024*1024;
    for(const auto& package:data.at("packages")) {
        bool selected=false;
        for(const auto& file:package.at("files")) { std::string name=file.at("path"); Path path=under(root,name); if(progress)progress("Checking files",0,0); check_cancelled();
            if(name=="DungeonRunners.exe"&&!existing_image.empty()) { if(existing_image!=updated_image)pending.insert(name); continue; }
            if(exists(path)&&(seed(name)||(length(path)==file.at("size").get<uint64_t>()&&file_hash(path)==file.at("sha256"))))continue;
            pending.insert(name);selected=true;
        }
        if(selected) { needed+=package.at("size").get<uint64_t>(); for(const auto& f:package.at("files"))needed+=f.at("size").get<uint64_t>(); }
    }
    ULARGE_INTEGER free{}; require(GetDiskFreeSpaceExW(root.c_str(),&free,nullptr,nullptr)&&free.QuadPart>=needed,"Not enough free disk space for a safe update.");
    Path tx=under(work,"transaction"); folders(under(tx,"new"));
    try {
        for(const auto& package:data.at("packages")) { bool selected=false; for(const auto& file:package.at("files")) if(pending.count(file.at("path")))selected=true; if(!selected)continue;
            std::string sha=package.at("sha256"); Path cached=under(work,"cache/"+sha+".zip");
            if(!exists(cached)||length(cached)!=package.at("size").get<uint64_t>()||file_hash(cached)!=sha) download(package.at("url"),cached,package.at("size"),progress);
            stage(package,cached,under(tx,"new"));
        }
        if(pending.count("DungeonRunners.exe")&&!existing_image.empty()) { require(file_hash(executable)==hash(existing_image),"The executable changed during update."); write(under(tx,"new/DungeonRunners.exe"),updated_image); }
        Path config=under(root,"config/DungeonRunners.cfg"); Bytes before=exists(config)?read(config,65536):Bytes{}; auto connected=connection_config(before); if(connected!=before) { pending.insert("config/DungeonRunners.cfg");write(under(tx,"new/config/DungeonRunners.cfg"),connected); }
        Json entries=Json::array(); for(const auto& name:pending) { Path target=under(root,name); entries.push_back({{"path",name},{"existed",exists(target)},{"previousHash",exists(target)?Json(file_hash(target)):Json(nullptr)},{"newHash",file_hash(under(tx,"new/"+name))}}); }
        check_cancelled();game_closed(); Json journal={{"committed",false},{"files",entries}}; atomic_write(under(tx,"journal.json"),bytes(journal.dump())); if(progress)progress("Installing",0,0);
        for(const auto& item:entries) { std::string name=item.at("path"); Path target=under(root,name),backup=under(tx,"previous/"+name),source=under(tx,"new/"+name); folders(directory(target));folders(directory(backup));
            if(item.at("existed").get<bool>()) { require(file_hash(target)==item.at("previousHash"),"A game file changed during update.");require(MoveFileW(target.c_str(),backup.c_str())!=0,"Cannot back up game file."); } else require(!exists(target),"A file appeared during update.");
            require(MoveFileW(source.c_str(),target.c_str())!=0,"Cannot install game file.");
        }
        runtime_folders(root);journal["committed"]=true;atomic_write(under(tx,"journal.json"),bytes(journal.dump()));atomic_write(saved,signed_bytes);remove_tree(tx);
    } catch(...) { recover(root);throw; }
}
void entry_points(const Path& root) {
    Lock lock(root);Path source=self(),target=under(root,"DungeonRunnersLauncher.exe"),marker=under(root,".dr-client/launcher.sha256");
    if(_wcsicmp(source.c_str(),target.c_str())) {
        if(exists(target))require(exists(marker)&&string(read(marker,128))==file_hash(target),"The installed launcher was modified. It was preserved.");
        atomic_write(target,read(source,268435456));
    }
    atomic_write(marker,bytes(file_hash(target)));atomic_write(under(local_data(),"Dungeon Runners Launcher/folder.txt"),bytes(utf8(root)));
    wchar_t desktop[MAX_PATH]; if(SUCCEEDED(SHGetFolderPathW(nullptr,CSIDL_DESKTOPDIRECTORY,nullptr,SHGFP_TYPE_CURRENT,desktop))) {
        Path shortcut=under(desktop,"Dungeon Runners Reborn.lnk"); if(!exists(shortcut)) {
            CoInitialize(nullptr); IShellLinkW* link=nullptr; if(SUCCEEDED(CoCreateInstance(CLSID_ShellLink,nullptr,CLSCTX_INPROC_SERVER,IID_IShellLinkW,reinterpret_cast<void**>(&link)))) {
                link->SetPath(target.c_str());link->SetWorkingDirectory(root.c_str());link->SetDescription(L"Dungeon Runners Reborn");link->SetIconLocation(target.c_str(),0); IPersistFile* persist=nullptr;
                if(SUCCEEDED(link->QueryInterface(IID_IPersistFile,reinterpret_cast<void**>(&persist)))){persist->Save(shortcut.c_str(),TRUE);persist->Release();}link->Release();
            }CoUninitialize();
        }
    }
}
void requirements(const Path& root,const Progress& progress) {
    wchar_t system[MAX_PATH]; require(GetSystemDirectoryW(system,MAX_PATH)>0,"Cannot locate Windows libraries.");
    if(exists(under(system,"d3dx9_31.dll"))&&exists(under(system,"d3dx9_40.dll")))return;
    game_closed(); Lock lock(root); if(progress)progress("Preparing DirectX",0,0);
    Path cache=under(root,".dr-client/directx-legacy");folders(cache);Path exe=under(cache,"directx.exe");
    const std::string digest="053f76dcbb28802e23341b6a787e3b0791c0fa5c8d4d011b1044172dbf89c73b";
    if(!exists(exe)||length(exe)!=100275120||file_hash(exe)!=digest) download("https://download.microsoft.com/download/8/4/a/84a35bf1-dafe-4ae8-82af-ad2ae20b6b14/directx_Jun2010_redist.exe",exe,100275120,progress);
    require(length(exe)==100275120&&file_hash(exe)==digest,"DirectX download verification failed."); check_cancelled();
    Path extracted=under(cache,"extracted");folders(extracted);require(start(exe,{L"/Q",L"/T:"+extracted},cache,true)==0,"DirectX extraction failed.");
    Path selected=under(cache,"required");folders(selected); for(const char* name:{"DXSETUP.exe","DSETUP.dll","dsetup32.dll","dxupdate.cab","OCT2006_d3dx9_31_x86.cab","Nov2008_d3dx9_40_x86.cab"}) { Path input=under(extracted,name),output=under(selected,name); require(exists(input)&&CopyFileW(input.c_str(),output.c_str(),FALSE),"DirectX extraction is incomplete."); }
    OSVERSIONINFOW os{};os.dwOSVersionInfoSize=sizeof os;GetVersionExW(&os);DWORD code=start(under(selected,"DXSETUP.exe"),{L"/silent"},selected,true,os.dwMajorVersion>=6);
    require((code==0||code==3010)&&exists(under(system,"d3dx9_31.dll"))&&exists(under(system,"d3dx9_40.dll")),"DirectX setup did not finish. Select Repair to retry.");
}
void play(const Path& root,const Progress& progress) {
    Path saved=under(root,".dr-client/manifest.json"); auto signed_bytes=exists(saved)?read(saved,131072):download(feed,131072); Json data=manifest(signed_bytes);
    { Lock lock(root); if(exists(under(root,".dr-client/transaction"))){game_closed();recover(root);}
      if(exists(under(root,".dr-client/android-addon-install.json"))||exists(under(root,".dr-client/addon-removal.json"))){game_closed();recover_addons(root);}
      for(const auto& package:data.at("packages"))for(const auto& file:package.at("files")) { std::string name=file.at("path");if(seed(name))continue;Path path=under(root,name);require(exists(path),"A game file is missing. Select Repair.");
        if(name=="DungeonRunners.exe")require(compatible(read(path,33554432),parse(resource(102)).at("installed")),"The game executable is incompatible. Select Repair.");
        else { require(length(path)==file.at("size").get<uint64_t>(),"A game file is incomplete. Select Repair.");if(!exists(saved)||name.find(".dll")!=std::string::npos)require(file_hash(path)==file.at("sha256"),"A game file failed verification. Select Repair."); }
      }
      runtime_folders(root);Path config=under(root,"config/DungeonRunners.cfg");Bytes original=exists(config)?read(config,65536):Bytes{},connected=connection_config(original);if(original!=connected){game_closed();atomic_write(config,connected);}if(!exists(saved))atomic_write(saved,signed_bytes);
    }
    requirements(root,progress);check_cancelled();start(under(root,"DungeonRunners.exe"),{L"ran_from_launcher"},root,false);
}
void installation_contracts(const Path& root) {
    require(!exists(root),"Contract fixture directory already exists.");folders(root);
    auto original=bytes("original"),replacement=bytes("replacement");
    Path archive=under(root,"fixture.zip"),stage_root=under(root,"stage");write(archive,resource(104));
    Json file={{"path","fmodex.dll"},{"size",replacement.size()},{"sha256",hash(replacement)},{"seed",false}};
    Json package={{"size",length(archive)},{"sha256",file_hash(archive)},{"files",Json::array({file})}};
    stage(package,archive,stage_root);require(read(under(stage_root,"fmodex.dll"),128)==replacement,"ZIP content mismatch.");
    Json bad=package;bad["files"][0]["path"]="../escape.dll";bool rejected=false;try{stage(bad,archive,under(root,"bad"));}catch(...){rejected=true;}require(rejected,"ZIP path substitution accepted.");
    bad=package;bad["sha256"]=std::string(64,'0');rejected=false;try{stage(bad,archive,under(root,"bad"));}catch(...){rejected=true;}require(rejected,"Corrupt package hash accepted.");
    auto config=connection_config(bytes("[ResourceManager]\nResourceConfig = Custom.cfg\n[AuthServer]\nAddress = old.invalid\nPort=123\n[Sound]\nVolume=0.7\n"));
    require(string(config).find("ResourceConfig = Custom.cfg")!=std::string::npos&&string(config).find("Volume=0.7")!=std::string::npos&&string(config).find("Address = play.dungeonrunnersreborn.com")!=std::string::npos&&string(config).find("old.invalid")==std::string::npos&&connection_config(config)==config,"Configuration preservation failed.");
    require(string(connection_config({})).find("ResourceConfig = ResourceManager.cfg")!=std::string::npos,"Fresh game configuration is incomplete.");
    Path tx=under(root,".dr-client/transaction"),target=under(root,"fmodex.dll"),backup=under(tx,"previous/fmodex.dll");
    Json journal={{"committed",false},{"files",Json::array({{{"path","fmodex.dll"},{"existed",true},{"previousHash",hash(original)},{"newHash",hash(replacement)}}})}};
    write(backup,original);write(target,replacement);write(under(root,"keep.txt"),bytes("user file"));atomic_write(under(tx,"journal.json"),bytes(journal.dump()));recover(root);
    require(read(target,128)==original&&!exists(tx)&&string(read(under(root,"keep.txt"),128))=="user file","Interrupted update rollback failed.");
    write(backup,original);write(target,bytes("user edit"));atomic_write(under(tx,"journal.json"),bytes(journal.dump()));rejected=false;try{recover(root);}catch(...){rejected=true;}require(rejected&&string(read(target,128))=="user edit"&&exists(backup),"Recovery overwrote a user edit.");
    remove_tree(root);
}
}
