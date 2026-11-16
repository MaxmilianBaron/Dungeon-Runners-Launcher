#include "launcher.h"

namespace dr {
namespace {
const char* graphics_state=".dr-client/graphics.json";
const char* graphics_journal=".dr-client/graphics-transaction.json";
const char* wrapper_hash="060cb161eb7a0c608a520185f0b334db325ad71d69016bc26060c9d5b9651f7d";
const char* archive_hash="9afa493396feec7108f42a2d97661eef96bbaac5addf0dfb5cfbc3001cd76a0a";
const char* archive_url="https://github.com/dege-diosg/dgVoodoo2/releases/download/2.87.6/dgVoodoo2_87_6.zip";
const uint64_t graphics_limit=16777216;
using GraphicsFiles=std::map<std::string,Bytes>;
struct GraphicsPlan {GraphicsFiles files;std::map<std::string,Json> before;};
using GraphicsGuard=std::function<void()>;
using GraphicsCheckpoint=std::function<void(const std::string&)>;
const std::set<std::string> addon_loaders={"c5c77c193b78add42741e0d0166c3ad04966422caeb04ecdea3e9339bc8d8a44",
"47a1fcb119f21230f9e60f6ebe4d65d3b49b18c7f7beb84a2a642b276e43fc8f",
"77e32182e46a854174527dcd6e80ba6903d64ea172c3bddb3f4e687cca77a31c",
"6bb1eaab6206ae4281b6da8412221d0873b91395dfd5c91e8737e78292101dd2",
"a8eee6d5895f7366868534693ff100e154f2cf95ba626e96f7250255cea7b2cb",
"ae9502f26738af307d3ff52f5d1642e75190d3893323a65a4cec199ef6a5f6e8",
"970fb4e9c4568bf67b9b3772c20422f53e59778413eeff7dd1bd0702d8d99e68",
"b0e5a647b5ec64dc15a94256fd369df4dc7d51cc1c06dd16efedaacccef6a65e",
"eb953e53fbf551f431eecaed106aab0818a3850bfc35b455d3a780f4378e6f2f",
"35130d9ec6a5150914f2a802264bdb21d22ca49a26efb32033727560e96d6825",
"943b9cc53197893df46ab71b20530cfa1c57464bbabb7d1c6a4b1f3c4557bf4d",
"57aae7c93b253ca15ec6b8a0bf428de286d9d24e5e8891d8c5f7742545749e53",
"d174d2ba881307288f5a8f7c8110ff5daab9f398dac3fdef999a31d1ab21a647"};
const std::set<std::string> original_graphics={"4fa74549c92ea7f95b16748aa16e83cd4b58a31326da5d85620043d85cd1fd47"};
bool graphics_path(const std::string& path){return path=="d3d9.dll"||path=="d3d9.previous.dll"||path=="dgVoodoo.conf"||path==graphics_state;}
Json graphics_hash(const Path& root,const std::string& relative){const auto path=under(root,relative);return exists(path)?Json(hash(read(path,graphics_limit))):Json(nullptr);}
bool known_graphics_hash(const Json& digest){return !digest.is_null()&&digest.is_string()&&(digest==wrapper_hash||addon_loaders.count(digest.get<std::string>())!=0||original_graphics.count(digest.get<std::string>())!=0);}
bool recognized_graphics(const Json& digest){return !digest.is_null()&&(digest==wrapper_hash||original_graphics.count(digest.get<std::string>())!=0);}
Json read_graphics(const Path& root){
    const auto path=under(root,graphics_state);
    if(!exists(path)){auto loader=graphics_hash(root,"d3d9.dll");bool active=loader==wrapper_hash||!loader.is_null()&&addon_loaders.count(loader.get<std::string>())!=0&&graphics_hash(root,"d3d9.previous.dll")==wrapper_hash;return Json({{"schema",1},{"mode",active?"dgVoodoo":"auto"},{"originalHash",nullptr}});}
    auto state=parse(read(path,4096));require(state.is_object()&&state.at("schema")==1&&state.contains("mode")&&(state.at("mode")=="auto"||state.at("mode")=="standard"||state.at("mode")=="dgVoodoo"),"Invalid graphics settings. Files were preserved.");
    if(!state.contains("originalHash"))state["originalHash"]=nullptr;
    const auto& original=state.at("originalHash");require(original.is_null()||original.is_string()&&original_graphics.count(original.get<std::string>())!=0,"Invalid graphics backup identity.");
    require(state.at("mode")!="standard"||original.is_null(),"Invalid standard graphics settings.");return state;
}
Path graphics_backup(const Path& root,const std::string& id){return under(root,".dr-client/graphics-backups/"+id);}
Path original_path(const Path& root,const std::string& digest){require(original_graphics.count(digest)!=0,"The preserved graphics library is not recognized.");return under(root,".dr-client/graphics-original/"+digest+".dll");}
Bytes wrapper_image(const Bytes& archive){
    require(archive.size()==9244362&&hash(archive)==archive_hash,"dgVoodoo download verification failed.");mz_zip_archive zip{};require(mz_zip_reader_init_mem(&zip,archive.data(),archive.size(),0)!=0,"Invalid dgVoodoo archive.");Bytes image;
    try{
        mz_uint count=mz_zip_reader_get_num_files(&zip);require(count>0&&count<=256,"Invalid dgVoodoo archive entry count.");bool found=false;
        for(mz_uint i=0;i<count;i++){
            mz_zip_archive_file_stat info{};require(mz_zip_reader_file_stat(&zip,i,&info)!=0,"Invalid dgVoodoo archive entry.");
            if(std::string(info.m_filename)!="MS/x86/D3D9.dll")continue;
            require(!found&&!info.m_is_directory&&!info.m_is_encrypted&&info.m_uncomp_size==482816&&((info.m_external_attr>>16)&0xf000)!=0xa000&&!(info.m_external_attr&0x400),"Invalid dgVoodoo library.");found=true;image.resize(482816);
            require(mz_zip_reader_extract_to_mem(&zip,i,image.data(),image.size(),0)!=0,"Cannot extract dgVoodoo library.");
        }
        require(found&&hash(image)==wrapper_hash,"dgVoodoo library verification failed.");
    }catch(...){mz_zip_reader_end(&zip);throw;}mz_zip_reader_end(&zip);return image;
}
Bytes obtain_wrapper(const Path& root,const Progress& progress){
    for(const char* name:{"d3d9.dll","d3d9.previous.dll"})if(graphics_hash(root,name)==wrapper_hash)return read(under(root,name),graphics_limit);
    const auto cached=under(root,std::string(".dr-client/graphics-cache/")+archive_hash+".zip");
    if(exists(cached)&&length(cached)==9244362){auto archive=read(cached,9244362);if(hash(archive)==archive_hash)return wrapper_image(archive);}
    if(progress)progress("Downloading dgVoodoo",0,0);auto archive=download(archive_url,9244362);auto image=wrapper_image(archive);atomic_write(cached,archive);return image;
}
void recover_graphics_transaction(const Path& root){
    const auto path=under(root,graphics_journal);if(!exists(path))return;
    auto journal=parse(read(path,16384));std::string id=journal.at("id");const auto& entries=journal.at("files");bool committed=journal.at("committed");
    require(id.size()==32&&id.find_first_not_of("0123456789abcdefABCDEF")==std::string::npos&&entries.is_array()&&!entries.empty()&&entries.size()<=4,"Invalid graphics recovery journal.");
    auto backup=graphics_backup(root,id);std::set<std::string> seen;GraphicsFiles originals;
    for(const auto& entry:entries){
        std::string name=entry.at("path");const auto& before=entry.at("before");const auto& after=entry.at("after");
        require(graphics_path(name)&&seen.insert(name).second&&(before.is_null()||before.is_string()&&valid_hash(before.get<std::string>()))&&(after.is_null()||after.is_string()&&valid_hash(after.get<std::string>()))&&(!before.is_null()||!after.is_null()),"Invalid graphics recovery file.");under(root,name);
        if(name=="d3d9.dll"||name=="d3d9.previous.dll")require((before.is_null()||known_graphics_hash(before))&&(after.is_null()||known_graphics_hash(after)),"Invalid graphics recovery library identity.");
        if(!committed){
            if(!before.is_null()){auto data=read(under(backup,name),graphics_limit);require(hash(data)==before,"Graphics recovery backup verification failed.");originals.emplace(name,std::move(data));}
            auto now=graphics_hash(root,name);require(now==before||now==after,"A graphics file changed during installation. Files and backups were preserved.");
        }
    }
    if(!committed)for(auto it=entries.rbegin();it!=entries.rend();++it){
        std::string name=it->at("path");const auto& before=it->at("before");const auto& after=it->at("after");auto now=graphics_hash(root,name);
        require(now==before||now==after,"A graphics file changed during recovery. Files and backups were preserved.");
        if(before.is_null()){if(!now.is_null())require(DeleteFileW(under(root,name).c_str())!=0,"Cannot restore graphics installation.");}
        else if(now!=before)atomic_write(under(root,name),originals.at(name));
    }
    folders(backup);auto receipt=under(backup,committed?"installed.json":"restored.json");require(!exists(receipt)&&MoveFileW(path.c_str(),receipt.c_str())!=0,"Cannot finish graphics recovery.");
}
void apply_graphics(const Path& root,GraphicsPlan plan,const GraphicsGuard& guard,const Progress& progress,const GraphicsCheckpoint& checkpoint={}){
    auto files=std::move(plan.files);require(files.size()<=4&&plan.before.size()==4,"Invalid graphics transaction.");
    for(const auto& observed:plan.before)require(graphics_path(observed.first)&&graphics_hash(root,observed.first)==observed.second,"Graphics files changed while preparing the operation. No graphics files were replaced.");
    for(auto it=files.begin();it!=files.end();){auto now=plan.before.at(it->first);if(it->second.empty()?now.is_null():now==hash(it->second))it=files.erase(it);else ++it;}if(files.empty())return;
    GUID guid;require(SUCCEEDED(CoCreateGuid(&guid)),"Cannot create graphics transaction.");std::string id=hex(reinterpret_cast<const unsigned char*>(&guid),sizeof guid);auto backup=graphics_backup(root,id);Json entries=Json::array();
    for(const auto& item:files){require(graphics_path(item.first)&&item.second.size()<=graphics_limit,"Invalid graphics transaction file.");auto before=plan.before.at(item.first);if(item.first=="d3d9.dll"||item.first=="d3d9.previous.dll")require(before.is_null()||known_graphics_hash(before),"An unknown graphics library was left untouched.");Json after=item.second.empty()?Json(nullptr):Json(hash(item.second));entries.push_back({{"path",item.first},{"before",before},{"after",after}});
        if(!before.is_null()){auto original=read(under(root,item.first),graphics_limit);require(hash(original)==before,"A graphics file changed before installation.");atomic_write(under(backup,item.first),original);}
    }
    check_cancelled();guard();for(const auto& entry:entries)require(graphics_hash(root,entry.at("path"))==entry.at("before"),"A graphics file changed before installation.");
    auto path=under(root,graphics_journal);require(!exists(path),"Recover the interrupted graphics operation first.");Json journal={{"id",id},{"committed",false},{"files",entries}};atomic_write(path,bytes(journal.dump()));
    try{
        if(checkpoint)checkpoint("journal");
        for(const auto& entry:entries){check_cancelled();guard();std::string name=entry.at("path");require(graphics_hash(root,name)==entry.at("before"),"A graphics file changed during installation.");if(progress)progress("Changing graphics",0,0);
            const auto& data=files.at(name);if(data.empty())require(DeleteFileW(under(root,name).c_str())!=0,"Cannot remove graphics library.");else atomic_write(under(root,name),data);if(checkpoint)checkpoint("write:"+name);
        }
        check_cancelled();if(checkpoint)checkpoint("commit");journal["committed"]=true;atomic_write(path,bytes(journal.dump()));recover_graphics_transaction(root);
    }catch(...){recover_graphics_transaction(root);throw;}
}
GraphicsPlan graphics_changes(const Path& root,GraphicsMode mode,bool enabled,const Bytes& wrapper){
    GraphicsPlan plan;for(const char* name:{"d3d9.dll","d3d9.previous.dll","dgVoodoo.conf",graphics_state})plan.before.emplace(name,graphics_hash(root,name));
    auto state=read_graphics(root);auto loader=plan.before.at("d3d9.dll"),previous=plan.before.at("d3d9.previous.dll");bool addons=!loader.is_null()&&addon_loaders.count(loader.get<std::string>())!=0;
    require(loader.is_null()||addons||recognized_graphics(loader),"An unknown d3d9.dll is installed. It was left untouched.");
    require(previous.is_null()||recognized_graphics(previous),"An unknown d3d9.previous.dll is installed. It was left untouched.");
    require(addons||previous.is_null()||loader.is_null()||previous==loader,"A different graphics backup already exists. Files were preserved.");
    Json original=state.at("originalHash"),active=addons?previous:loader;auto& changes=plan.files;
    if(!original.is_null()){auto backup=original_path(root,original.get<std::string>());require(exists(backup)&&hash(read(backup,graphics_limit))==original,"The original graphics backup is missing or damaged.");}
    if(enabled){
        require(hash(wrapper)==wrapper_hash,"Invalid dgVoodoo library.");
        if(active.is_null()&&!addons&&!previous.is_null())active=previous;
        if(!active.is_null()&&active!=wrapper_hash){require(original.is_null()||original==active,"The graphics backend changed outside the launcher. Files were preserved.");original=active;auto path=original_path(root,original.get<std::string>());
            if(exists(path))require(hash(read(path,graphics_limit))==original,"The original graphics backup was modified.");
            else{auto data=read(under(root,addons||loader.is_null()?"d3d9.previous.dll":"d3d9.dll"),graphics_limit);require(hash(data)==original,"The graphics backend changed before installation.");atomic_write(path,data);}
        }
        changes[addons?"d3d9.previous.dll":"d3d9.dll"]=wrapper;
        if(!addons&&!previous.is_null())changes["d3d9.previous.dll"]=Bytes{};
        if(plan.before.at("dgVoodoo.conf").is_null())changes["dgVoodoo.conf"]=bytes("Version = 0x287\n[General]\nOutputAPI = bestavailable\n[DirectX]\nResolution = unforced\n");
    }else{
        require(original.is_null()||active.is_null()||active==wrapper_hash||active==original,"The graphics backend changed outside the launcher. Files were preserved.");
        if(!original.is_null())changes[addons?"d3d9.previous.dll":"d3d9.dll"]=read(original_path(root,original.get<std::string>()),graphics_limit);
        else if(active==wrapper_hash)changes[addons?"d3d9.previous.dll":"d3d9.dll"]=Bytes{};
        if(!addons&&previous==wrapper_hash)changes["d3d9.previous.dll"]=Bytes{};
        original=nullptr;
    }
    changes[graphics_state]=bytes(Json({{"schema",1},{"mode",mode==GraphicsMode::Auto?"auto":mode==GraphicsMode::DgVoodoo?"dgVoodoo":"standard"},{"originalHash",original}}).dump());
    for(const auto& observed:plan.before)require(graphics_hash(root,observed.first)==observed.second,"Graphics files changed while preparing the operation. No graphics files were replaced.");return plan;
}
}
bool graphics_supported(){OSVERSIONINFOW os{};os.dwOSVersionInfoSize=sizeof os;return GetVersionExW(&os)&&(os.dwMajorVersion>6||(os.dwMajorVersion==6&&os.dwMinorVersion>=1));}
GraphicsMode graphics_mode(const Path& root){const auto mode=read_graphics(root).at("mode");return mode=="auto"?GraphicsMode::Auto:mode=="dgVoodoo"?GraphicsMode::DgVoodoo:GraphicsMode::Standard;}
bool graphics_arm64(){
    using Query=BOOL(WINAPI*)(HANDLE,USHORT*,USHORT*);auto kernel=GetModuleHandleW(L"kernel32.dll");auto query=reinterpret_cast<Query>(kernel?GetProcAddress(kernel,"IsWow64Process2"):nullptr);
    USHORT process=0,native=0;if(query&&query(GetCurrentProcess(),&process,&native))return native==0xaa64;
    SYSTEM_INFO info{};GetNativeSystemInfo(&info);return info.wProcessorArchitecture==12;
}
bool graphics_enabled(const Path& root){auto mode=graphics_mode(root);return mode==GraphicsMode::DgVoodoo||(mode==GraphicsMode::Auto&&graphics_supported()&&graphics_arm64());}
bool graphics_pending(const Path& root){return exists(under(root,graphics_journal));}
void recover_graphics(const Path& root){recover_graphics_transaction(root);}
void set_graphics(const Path& root,GraphicsMode mode,const Progress& progress){
    require(mode==GraphicsMode::Auto||mode==GraphicsMode::Standard||mode==GraphicsMode::DgVoodoo,"Invalid graphics mode.");
    bool enabled=mode==GraphicsMode::DgVoodoo||(mode==GraphicsMode::Auto&&graphics_supported()&&graphics_arm64());
    require(!enabled||graphics_supported(),"dgVoodoo requires Windows 7 or newer.");game_closed();installation_lock(root,[&]{recover(root);recover_addons(root);recover_graphics(root);Bytes image;
        if(enabled)image=obtain_wrapper(root,progress);
        auto changes=graphics_changes(root,mode,enabled,image);check_cancelled();game_closed();apply_graphics(root,std::move(changes),game_closed,progress);
    });
}
void ensure_graphics(const Path& root,const Progress& progress){
    if(graphics_pending(root)){game_closed();installation_lock(root,[&]{recover(root);recover_addons(root);recover_graphics(root);});}
    const auto state=read_graphics(root);bool enabled=graphics_enabled(root);auto loader=graphics_hash(root,"d3d9.dll"),previous=graphics_hash(root,"d3d9.previous.dll");
    bool addons=!loader.is_null()&&addon_loaders.count(loader.get<std::string>())!=0;bool active=(addons?previous:loader)==wrapper_hash;
    bool ready=active&&(addons||previous.is_null())&&exists(under(root,graphics_state));auto config=under(root,"dgVoodoo.conf");ready=ready&&exists(config)&&!(GetFileAttributesW(config.c_str())&FILE_ATTRIBUTE_DIRECTORY);
    const auto& original=state.at("originalHash");if(ready&&!original.is_null()){auto backup=original_path(root,original.get<std::string>());ready=exists(backup)&&hash(read(backup,graphics_limit))==original;}
    if(enabled?ready:!active&&(previous!=wrapper_hash||!loader.is_null())&&original.is_null())return;
    set_graphics(root,graphics_mode(root),progress);
}

}
