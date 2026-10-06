#include "launcher.h"

namespace dr {
int run_tests(const Path& output) {
    std::vector<std::string> passed;
    auto rejects=[&](const char* name,const std::function<void()>& action){bool rejected=false;try{action();}catch(const std::exception&){rejected=true;}require(rejected,std::string("Test failed: ")+name);passed.push_back(name);};
    require(!modern_platform(5,1,PROCESSOR_ARCHITECTURE_INTEL)&&!modern_platform(6,0,PROCESSOR_ARCHITECTURE_AMD64)&&!modern_platform(6,1,PROCESSOR_ARCHITECTURE_AMD64)&&!modern_platform(6,2,PROCESSOR_ARCHITECTURE_AMD64)&&!modern_platform(6,3,PROCESSOR_ARCHITECTURE_AMD64)&&!modern_platform(10,0,PROCESSOR_ARCHITECTURE_INTEL)&&modern_platform(10,0,PROCESSOR_ARCHITECTURE_AMD64)&&!modern_platform(10,0,12,19041)&&modern_platform(10,0,12,22000),"Operating system routing failed.");passed.push_back("XP, Vista, 7, 8, 8.1, x86, x64 and ARM64 routing");
    require(hash(bytes("abc"))=="ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad","SHA-256 vector failed.");passed.push_back("SHA-256 known vector");
    require(allowed_url(feed)&&allowed_url("https://release-assets.githubusercontent.com/example?token=value"),"Download allowlist rejected release hosts.");
    rejects("HTTP downgrade rejected",[]{require(allowed_url("http://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest"),"Rejected");});
    for(const char* bad:{"https://github.com.evil.example/MaxmilianBaron/Dungeon-Runners-Launcher/releases/1","https://github.com/another/repository/releases/1","https://user@github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/1","https://github.com:8443/MaxmilianBaron/Dungeon-Runners-Launcher/releases/1","https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher/releases/1\r\nHeader: value"})rejects("Untrusted redirect rejected",[&]{require(allowed_url(bad),"Rejected");});
    for(const char* bad:{"../outside.exe","config/../../outside.exe","/absolute.exe","C:/outside.exe","file:stream","Addons//file","NUL.txt","config/COM1.cfg","config/file. ","config\\file"})rejects("Unsafe package path rejected",[&]{under(L"C:\\Example",bad);});
    rejects("Drive root installation rejected",[]{game_root(L"C:\\");});
    rejects("System directory installation rejected",[]{wchar_t system[MAX_PATH];GetWindowsDirectoryW(system,MAX_PATH);game_root(Path(system)+L"\\example");});
    rejects("Forged signature rejected",[]{manifest(bytes("{\"payload\":\"e30=\",\"signature\":\"AA==\"}"));});
    rejects("Oversized manifest rejected",[]{manifest(Bytes(131073));});
    rejects("Deep JSON rejected",[]{parse(bytes(std::string(34,'[')+"0"+std::string(34,']')));});
    rejects("Duplicate JSON fields rejected",[]{parse(bytes("{\"schema\":1,\"schema\":2}"));});
    Json profile={{"schema",1},{"minimumSize",3},{"maximumSize",3},{"ranges",Json::array({{{"offset",0},{"length",3},{"sha256",Json::array({hash(bytes("abc"))})}}})}};
    require(compatible(bytes("abc"),profile)&&!compatible(bytes("abd"),profile),"Protected client range verification failed.");passed.push_back("Client protected ranges reject changed bytes");
    require(quote(L"a b\\")==L"\"a b\\\\\""&&quote(L"a\"b")==L"\"a\\\"b\"","Windows argument quoting failed.");passed.push_back("Structured process arguments");
    Path fixtures=under(directory(absolute(output)),"contracts-"+std::to_string(GetCurrentProcessId()));
    installation_contracts(fixtures);passed.push_back("ZIP allowlist, integrity, configuration preservation and interrupted update recovery");
    write(output,bytes(Json({{"passed",passed.size()},{"checks",passed}}).dump(2)));return 0;
}
}
