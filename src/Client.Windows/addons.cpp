#include "launcher.h"

namespace dr {
namespace {
const uint64_t maximum_file=33554432;
const char* api="https://api.github.com/repos/MaxmilianBaron/Dungeon-Runners-Addons/releases/latest";
const char* install_journal=".dr-client/android-addon-install.json";
const char* removal_journal=".dr-client/addon-removal.json";
using Files=std::map<std::string,Bytes>;
using Guard=std::function<void()>;
using Checkpoint=std::function<void(const std::string&)>;
struct Asset {std::string url,digest;uint64_t size;};
std::string lower(std::string value) {std::transform(value.begin(),value.end(),value.begin(),[](unsigned char c){return static_cast<char>(tolower(c));});return value;}
bool ends(const std::string& value,const std::string& suffix) {return value.size()>=suffix.size()&&value.compare(value.size()-suffix.size(),suffix.size(),suffix)==0;}
bool allowed(const std::string& path) {
    if(path=="d3d9.dll"||path=="d3d9.previous.dll"||path=="Addons/Runtime/Addons.dll"||path=="Addons/Runtime/ui.bin"||path=="Addons/Runtime/ui-resources.json"||path=="Addons/Runtime/Update.ps1"||path=="Addons/Update.cmd")return true;
    return std::count(path.begin(),path.end(),'/')==2&&((path.find("Addons/Licenses/")==0&&ends(path,".txt"))||(path.find("Addons/")==0&&ends(path,"/addon.ini")));
}
bool preserve(const std::string& path) {return ends(lower(path),".ini")||path.find("Addons/Licenses/")==0;}
Bytes addon_read(const Path& path) {auto data=read(path,maximum_file);require(!data.empty(),"Invalid empty addon file.");return data;}
Json current(const Path& root,const std::string& path) {Path file=under(root,path);return exists(file)?Json(hash(addon_read(file))):Json(nullptr);}
std::string identity() {GUID id;require(SUCCEEDED(CoCreateGuid(&id)),"Cannot create addon transaction.");return hex(reinterpret_cast<const unsigned char*>(&id),sizeof id);}
Path backup_path(const Path& root,const std::string& id,bool removal) {return under(root,std::string(removal?".dr-client/addon-backups/":".dr-client/android-addon-backups/")+id);}
std::set<std::string> hashes(const Json& manifest,const char* key) {
    const auto& list=manifest.at(key);require(list.is_array()&&list.size()<=64,"Invalid addon compatibility list.");std::set<std::string> result;
    for(const auto& value:list){std::string digest=value;require(valid_hash(digest)&&result.insert(digest).second,"Invalid addon compatibility checksum.");}return result;
}
Asset asset(const Json& release,const std::string& name) {
    std::string tag=release.at("tag_name");require(!tag.empty()&&tag.size()<=64&&tag.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-")==std::string::npos&&!release.at("draft").get<bool>()&&!release.at("prerelease").get<bool>(),"A stable addon release is required.");
    const auto& candidates=release.at("assets");require(candidates.is_array()&&candidates.size()<=128,"Invalid addon release assets.");Json found;
    for(const auto& item:candidates)if(item.at("name")==name){require(found.is_null(),"Duplicate addon release asset.");found=item;}
    require(!found.is_null(),"The addon release is incomplete.");std::string url=found.at("browser_download_url"),digest=found.at("digest");uint64_t size=found.at("size");
    require(size>0&&size<=maximum_file&&url=="https://github.com/MaxmilianBaron/Dungeon-Runners-Addons/releases/download/"+tag+"/"+name&&digest.find("sha256:")==0&&valid_hash(digest.substr(7)),"The addon release has no valid download checksum.");
    return {url,digest.substr(7),size};
}
Bytes fetch(const Asset& item,uint64_t limit=maximum_file) {
    require(item.size<=limit,"Addon release asset is too large.");auto data=download(item.url,item.size);require(data.size()==item.size&&hash(data)==item.digest,"Addon download verification failed.");return data;
}
Json public_manifest() {auto release=parse(download(api,1048576));return parse(fetch(asset(release,"package.json"),131072));}
void verify_client(const Path& root,const Json& manifest) {
    auto image=addon_read(under(root,"DungeonRunners.exe"));bool valid=false;
    if(manifest.contains("clientCompatibility"))valid=compatible(image,manifest.at("clientCompatibility"));
    else {auto clients=hashes(manifest,"clients");valid=clients.count(hash(image))!=0;}
    require(valid,"This addon release does not support the installed game executable. Your files were preserved.");
}
Files archive_files(const Bytes& archive,const Json& manifest,const Path& root) {
    require(!archive.empty()&&archive.size()<=maximum_file,"Invalid addon archive size.");mz_zip_archive zip{};
    require(mz_zip_reader_init_mem(&zip,archive.data(),archive.size(),0)!=0,"Invalid addon archive.");Files files;
    try {
        mz_uint count=mz_zip_reader_get_num_files(&zip);require(count>=2&&count<=512,"Invalid addon archive entry count.");
        std::map<std::string,mz_uint> indices;std::set<std::string> seen;uint64_t size=0;
        for(mz_uint i=0;i<count;i++) {
            mz_zip_archive_file_stat info{};require(mz_zip_reader_file_stat(&zip,i,&info)!=0,"Invalid addon archive entry.");std::string path=info.m_filename;
            require(mz_zip_reader_get_filename(&zip,i,nullptr,0)==path.size()+1&&!info.m_is_directory&&!info.m_is_encrypted&&((info.m_external_attr>>16)&0xf000)!=0xa000&&!(info.m_external_attr&0x400)&&info.m_uncomp_size<=maximum_file&&seen.insert(lower(path)).second,"Invalid, linked or duplicate addon archive entry.");
            under(root,path);size+=info.m_uncomp_size;require(size<=67108864,"The addon archive is too large.");indices.emplace(path,i);
        }
        require(indices.count("Install.ps1")&&indices.count("package.json"),"The addon package is incomplete.");
        auto extract=[&](const std::string& path){auto it=indices.find(path);require(it!=indices.end(),"An addon file is missing from the archive.");mz_zip_archive_file_stat info{};require(mz_zip_reader_file_stat(&zip,it->second,&info)!=0&&info.m_uncomp_size>0,"Invalid addon file.");Bytes data(static_cast<size_t>(info.m_uncomp_size));require(mz_zip_reader_extract_to_mem(&zip,it->second,data.data(),data.size(),0)!=0,"Addon extraction verification failed.");return data;};
        require(parse(extract("package.json"))==manifest,"Addon package manifest mismatch.");
        const auto& entries=manifest.at("files");require(entries.is_array()&&entries.size()>=2&&entries.size()<=128,"Invalid addon file count.");seen.clear();
        for(const auto& entry:entries) {
            check_cancelled();std::string path=entry.at("path"),digest=entry.at("sha256");
            require(allowed(path)&&path!="d3d9.previous.dll"&&valid_hash(digest)&&seen.insert(lower(path)).second,"Invalid addon file identity.");
            auto data=extract(path);require(hash(data)==digest,"Addon file verification failed.");files.emplace(path,std::move(data));
        }
        for(const auto& entry:indices)if(entry.first.find("Addons/Licenses/")==0) {
            require(allowed(entry.first),"Invalid addon license.");if(!files.count(entry.first))files.emplace(entry.first,extract(entry.first));
        }
        require(files.count("d3d9.dll")&&files.count("Addons/Runtime/Addons.dll")&&files.count("Addons/Runtime/ui-resources.json")&&files.count("Addons/Licenses/LICENSE.txt"),"Addon runtime or license is missing.");
    }catch(...){mz_zip_reader_end(&zip);throw;}
    mz_zip_reader_end(&zip);return files;
}
Files prepare(const Path& root,const Json& manifest,Files files) {
    auto loaders=hashes(manifest,"loaders"),graphics=hashes(manifest,"chainLoaders");require(loaders.count(hash(files.at("d3d9.dll")))!=0,"The package loader is not recognized.");
    Path loader=under(root,"d3d9.dll");
    if(exists(loader)) {
        auto original=addon_read(loader);std::string digest=hash(original);require(loaders.count(digest)||graphics.count(digest),"An unknown d3d9.dll is installed. It was left untouched.");
        if(graphics.count(digest)) {Path previous=under(root,"d3d9.previous.dll");require(!exists(previous)||hash(addon_read(previous))==digest,"A different graphics backup already exists.");files.emplace("d3d9.previous.dll",std::move(original));}
    }
    for(auto it=files.begin();it!=files.end();) {Path target=under(root,it->first);if(exists(target)&&(preserve(it->first)||hash(addon_read(target))==hash(it->second)))it=files.erase(it);else ++it;}
    return files;
}
void recover_one(const Path& root,bool removal) {
    Path path=under(root,removal?removal_journal:install_journal);if(!exists(path))return;
    Json journal=parse(read(path,removal?16384:131072));std::string id=journal.at("id");const auto& entries=journal.at("files");bool committed=journal.at("committed");
    require(id.size()==32&&id.find_first_not_of("0123456789abcdefABCDEF")==std::string::npos&&entries.is_array()&&!entries.empty()&&entries.size()<=(removal?2u:160u),"Invalid addon recovery journal.");
    Path backup=backup_path(root,id,removal);std::set<std::string> seen;Files originals;
    for(const auto& entry:entries) {
        std::string name=entry.at("path");Json before=entry.at(removal?"hash":"before"),after=entry.at(removal?"replacementHash":"after");
        require(allowed(name)&&(!removal||name=="d3d9.dll"||name=="Addons/Runtime/Addons.dll")&&seen.insert(lower(name)).second&&(before.is_null()?!removal:valid_hash(before))&&(after.is_null()?removal:valid_hash(after))&&(!removal||after.is_null()||name=="d3d9.dll"),"Invalid addon recovery file.");under(root,name);
        if(!committed) {
            if(!before.is_null()) {auto data=addon_read(under(backup,name));require(hash(data)==before,"Addon recovery backup verification failed.");originals.emplace(name,std::move(data));}
            auto now=current(root,name);require(now==before||now==after||(removal&&now.is_null()),"An addon was edited during installation. Files and backups were preserved.");
        }
    }
    if(!committed)for(auto it=entries.rbegin();it!=entries.rend();++it) {
        std::string name=it->at("path");Json before=it->at(removal?"hash":"before"),after=it->at(removal?"replacementHash":"after"),now=current(root,name);
        require(now==before||now==after||(removal&&now.is_null()),"An addon changed during recovery. Backup preserved.");
        if(before.is_null()){if(!now.is_null())require(DeleteFileW(under(root,name).c_str())!=0,"Cannot restore addon installation.");}
        else if(now!=before)atomic_write(under(root,name),originals.at(name));
    }
    folders(backup);Path receipt=under(backup,committed?(removal?"removed.json":"installed.json"):"restored.json");require(!exists(receipt)&&MoveFileW(path.c_str(),receipt.c_str())!=0,"Cannot finish addon recovery.");
}
void apply(const Path& root,const Files& files,bool removal,const Guard& guard,const Progress& progress={},const Checkpoint& checkpoint={}) {
    if(files.empty())return;require(files.size()<=(removal?2u:160u),"Invalid addon transaction.");std::set<std::string> seen;Json entries=Json::array();
    std::string id=identity();Path backup=backup_path(root,id,removal);
    for(const auto& file:files) {
        require(allowed(file.first)&&(!removal||file.first=="d3d9.dll"||file.first=="Addons/Runtime/Addons.dll")&&(removal||!file.second.empty())&&file.second.size()<=maximum_file&&seen.insert(lower(file.first)).second,"Invalid addon transaction file.");
        Json before=current(root,file.first),after=file.second.empty()?Json(nullptr):Json(hash(file.second));require(!removal||!before.is_null(),"The addon selected for removal is missing.");
        entries.push_back({{"path",file.first},{removal?"hash":"before",before},{removal?"replacementHash":"after",after}});
        if(!before.is_null()){auto original=addon_read(under(root,file.first));require(hash(original)==before,"An addon changed before installation.");atomic_write(under(backup,file.first),original);}
    }
    check_cancelled();guard();for(const auto& entry:entries)require(current(root,entry.at("path"))==entry.at(removal?"hash":"before"),"An addon changed before installation.");
    Json journal={{"id",id},{"committed",false},{"files",entries}};Path path=under(root,removal?removal_journal:install_journal);require(!exists(path),"Recover the interrupted addon operation first.");atomic_write(path,bytes(journal.dump()));
    try {
        if(checkpoint)checkpoint("journal");uint64_t done=0;
        for(const auto& entry:entries) {
            check_cancelled();guard();std::string name=entry.at("path");require(current(root,name)==entry.at(removal?"hash":"before"),"An addon changed during installation.");
            if(progress)progress(removal?"Uninstalling addons":"Installing addons",done,files.size());
            const auto& data=files.at(name);if(data.empty())require(DeleteFileW(under(root,name).c_str())!=0,"Cannot remove addon runtime.");else atomic_write(under(root,name),data);
            if(checkpoint)checkpoint("write:"+name);done++;
        }
        check_cancelled();if(checkpoint)checkpoint("commit");journal["committed"]=true;atomic_write(path,bytes(journal.dump()));recover_one(root,removal);
    }catch(...){recover_one(root,removal);throw;}
}
Files removal_files(const Path& root,const Json& manifest) {
    auto loaders=hashes(manifest,"loaders"),graphics=hashes(manifest,"chainLoaders");Files files;auto loader=current(root,"d3d9.dll");
    require(loader.is_null()||loaders.count(loader.get<std::string>())||graphics.count(loader.get<std::string>()),"The installed d3d9.dll is not recognized. No files were removed.");
    if(exists(under(root,"Addons/Runtime/Addons.dll")))files.emplace("Addons/Runtime/Addons.dll",Bytes{});
    if(!loader.is_null()&&!graphics.count(loader.get<std::string>())) {
        Bytes replacement;Path previous=under(root,"d3d9.previous.dll");if(exists(previous)){replacement=addon_read(previous);require(graphics.count(hash(replacement))!=0,"The preserved graphics library is not recognized. No files were removed.");}
        files.emplace("d3d9.dll",std::move(replacement));
    }
    return files;
}
Files public_files(const Path& root,Json& manifest,const Progress& progress) {
    if(progress)progress("Checking addons",0,0);auto release=parse(download(api,1048576));manifest=parse(fetch(asset(release,"package.json"),131072));verify_client(root,manifest);
    if(progress)progress("Downloading addons",0,0);auto archive=fetch(asset(release,"Dungeon-Runners-Addons.zip"));auto files=archive_files(archive,manifest,root);
    if(progress)progress("Preparing addons",0,0);files["Addons/Runtime/ui.bin"]=addon_skin(root,files.at("Addons/Runtime/ui-resources.json"));return files;
}
}
void recover_addons(const Path& root) {recover_one(root,false);recover_one(root,true);}
void install_addons(const Path& root,const Progress& progress) {
    game_closed();installation_lock(root,[&]{recover(root);recover_addons(root);Json manifest;auto files=public_files(root,manifest,progress);files=prepare(root,manifest,std::move(files));check_cancelled();game_closed();verify_client(root,manifest);apply(root,files,false,game_closed,progress);});
}
void remove_addons(const Path& root,const Progress& progress) {
    game_closed();installation_lock(root,[&]{recover(root);recover_addons(root);if(progress)progress("Checking addons",0,0);auto manifest=public_manifest();apply(root,removal_files(root,manifest),true,game_closed,progress);});
}
void verify_addon_package(const Path& root,const Path& result) {
    Json manifest;auto files=public_files(root,manifest,{});Json checked=Json::array();for(const auto& file:files)checked.push_back({{"path",file.first},{"sha256",hash(file.second)},{"size",file.second.size()}});
    write(result,bytes(Json({{"version",manifest.at("version")},{"clientVerified",true},{"uiVerified",true},{"files",checked}}).dump(2)));
}
void addon_contracts(const Path& root) {
    require(!exists(root),"Addon fixture directory already exists.");folders(root);Guard guard=[]{};
    auto rejects=[&](const std::function<void()>& action){bool rejected=false;try{action();}catch(const std::exception&){rejected=true;}require(rejected,"Addon contract did not reject invalid input.");};
    const std::string runtime="Addons/Runtime/Addons.dll",setting="Addons/Loadouts/addon.ini";
    Files first={{"d3d9.dll",bytes("loader")},{runtime,bytes("runtime")},{setting,bytes("user settings")},{"Addons/Licenses/LICENSE.txt",bytes("license")}};
    apply(root,first,false,guard);for(const auto& file:first)require(addon_read(under(root,file.first))==file.second,"Fresh addon installation failed.");
    Json manifest={{"loaders",Json::array({hash(bytes("loader")),hash(bytes("new loader"))})},{"chainLoaders",Json::array({hash(bytes("graphics"))})}};
    Files update=first;update[setting]=bytes("defaults");update["Addons/Licenses/LICENSE.txt"]=bytes("new license");update[runtime]=bytes("updated");
    auto pending=prepare(root,manifest,update);require(pending.size()==1&&pending.count(runtime),"Addon settings preservation failed.");
    for(const std::string& point:{std::string("journal"),std::string("write:")+runtime,std::string("commit")}) {
        rejects([&]{apply(root,pending,false,guard,{},[&](const std::string& name){if(name==point)throw std::runtime_error("Simulated interruption");});});
        require(addon_read(under(root,runtime))==bytes("runtime")&&!exists(under(root,install_journal)),"Interrupted addon update did not restore original files.");
    }
    apply(root,pending,false,guard);require(addon_read(under(root,runtime))==bytes("updated")&&addon_read(under(root,setting))==bytes("user settings"),"Addon update failed.");
    write(under(root,"Addons/Loadouts/characters.json"),bytes("saved loadouts"));
    auto removed=removal_files(root,manifest);rejects([&]{apply(root,removed,true,guard,{},[&](const std::string& name){if(name=="write:"+runtime)throw std::runtime_error("Interrupted removal");});});
    require(addon_read(under(root,runtime))==bytes("updated"),"Interrupted addon removal lost runtime.");
    apply(root,removed,true,guard);require(!exists(under(root,runtime))&&!exists(under(root,"d3d9.dll"))&&addon_read(under(root,setting))==bytes("user settings")&&addon_read(under(root,"Addons/Loadouts/characters.json"))==bytes("saved loadouts"),"Addon removal lost user data.");
    write(under(root,"d3d9.dll"),bytes("graphics"));auto graphics=prepare(root,manifest,first);require(graphics.at("d3d9.previous.dll")==bytes("graphics"),"Graphics chaining failed.");apply(root,graphics,false,guard);apply(root,removal_files(root,manifest),true,guard);
    require(addon_read(under(root,"d3d9.dll"))==bytes("graphics"),"Uninstall did not restore graphics wrapper.");
    write(under(root,"d3d9.dll"),bytes("unknown"));rejects([&]{prepare(root,manifest,first);});rejects([&]{removal_files(root,manifest);});
    require(addon_read(under(root,"d3d9.dll"))==bytes("unknown"),"Unknown graphics wrapper was modified.");
    rejects([&]{apply(root,{{"../outside.dll",bytes("bad")}},false,guard);});
    rejects([&]{apply(root,{{"Addons/Loadouts/../../outside/addon.ini",bytes("bad")}},false,guard);});
    rejects([&]{apply(root,{{runtime,bytes("a")},{"Addons/Runtime/addons.dll",bytes("b")}},false,guard);});
    Files cancellation={{runtime,bytes("runtime")}};rejects([&]{apply(root,cancellation,false,guard,{},[](const std::string& name){if(name=="journal")InterlockedExchange(&cancelled,1);});});InterlockedExchange(&cancelled,0);
    require(!exists(under(root,runtime)),"Cancellation left a partial addon install.");
    Json entries=Json::array({{{"path",runtime},{"before",nullptr},{"after",hash(bytes("candidate"))}}});
    atomic_write(under(root,install_journal),bytes(Json({{"id",identity()},{"committed",false},{"files",entries}}).dump()));write(under(root,runtime),bytes("external edit"));
    rejects([&]{recover_addons(root);});require(addon_read(under(root,runtime))==bytes("external edit")&&exists(under(root,install_journal)),"Recovery overwrote a user edit.");
    Path assets=under(root,"assets");Json release={{"tag_name","v1"},{"draft",false},{"prerelease",false},{"assets",Json::array({{{"name","package.json"},{"browser_download_url","https://github.com/MaxmilianBaron/Dungeon-Runners-Addons/releases/download/v1/package.json"},{"size",3},{"digest","sha256:"+hash(bytes("abc"))}}})}};
    require(asset(release,"package.json").digest==hash(bytes("abc")),"Valid addon asset rejected.");release["assets"][0]["browser_download_url"]="https://example.com/package.json";rejects([&]{asset(release,"package.json");});
    rejects([&]{archive_files(resource(104),Json::object(),assets);});
}
}
