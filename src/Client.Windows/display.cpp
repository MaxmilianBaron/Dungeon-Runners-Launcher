#include "launcher.h"
#include <cctype>

namespace dr {
struct Coordinates {
    using Change=HANDLE(WINAPI*)(HANDLE);
    Change change=nullptr;HANDLE previous=nullptr;
    Coordinates(){change=reinterpret_cast<Change>(GetProcAddress(GetModuleHandleW(L"user32.dll"),"SetThreadDpiAwarenessContext"));if(change)previous=change(reinterpret_cast<HANDLE>(static_cast<intptr_t>(-4)));}
    ~Coordinates(){if(change&&previous)change(previous);}
};
static BOOL CALLBACK monitor_callback(HMONITOR handle,HDC,LPRECT,LPARAM parameter){
    MONITORINFOEXW info{};info.cbSize=sizeof info;
    if(GetMonitorInfoW(handle,&info))reinterpret_cast<std::vector<GameMonitor>*>(parameter)->push_back({info.szDevice,info.rcMonitor,info.rcWork,(info.dwFlags&MONITORINFOF_PRIMARY)!=0});
    return TRUE;
}
std::vector<GameMonitor> game_monitors(){Coordinates coordinates;std::vector<GameMonitor> result;EnumDisplayMonitors(nullptr,nullptr,monitor_callback,reinterpret_cast<LPARAM>(&result));std::sort(result.begin(),result.end(),[](const GameMonitor& a,const GameMonitor& b){return a.id<b.id;});return result;}
std::string selected_monitor(){
    try{Path path=under(local_data(),"Dungeon Runners Launcher/display.json");if(!exists(path))return "";auto data=parse(read(path,4096));std::string id=data.at("monitor");if(id.size()>256||std::any_of(id.begin(),id.end(),[](unsigned char value){return value<32;}))return "";return id;}catch(...){return "";}
}
void save_monitor(const std::string& selection){require(selection.size()<=256&&!std::any_of(selection.begin(),selection.end(),[](unsigned char value){return value<32;}),"Invalid monitor selection.");atomic_write(under(local_data(),"Dungeon Runners Launcher/display.json"),bytes(Json({{"monitor",selection}}).dump()));}
static std::string trim(std::string value){auto begin=value.find_first_not_of(" \t");if(begin==std::string::npos)return "";return value.substr(begin,value.find_last_not_of(" \t")-begin+1);}
static std::string lower(std::string value){for(auto& item:value)item=static_cast<char>(std::tolower(static_cast<unsigned char>(item)));return value;}
struct Line{std::string text,end;};
static int dimension(const std::map<std::string,std::string>& values,const char* key,int fallback){try{auto found=values.find(key);if(found==values.end())return fallback;size_t used=0;int value=std::stoi(found->second,&used);return value>0&&used==found->second.size()?value:fallback;}catch(...){return fallback;}}
static std::pair<int,int> dimensions(const RECT& area,int width,int height){int availableWidth=std::max(1L,area.right-area.left-32),availableHeight=std::max(1L,area.bottom-area.top-64);return {std::max(std::min(320,availableWidth),std::min(width,availableWidth)),std::max(std::min(240,availableHeight),std::min(height,availableHeight))};}
static Bytes profile(const Bytes& previous,const GameMonitor& monitor){
    require(previous.size()<=1048576,"Game display settings are too large.");require(previous.size()<2||!((previous[0]==255&&previous[1]==254)||(previous[0]==254&&previous[1]==255)),"The display configuration has an unsupported encoding.");
    std::string original=string(previous),bom; if(original.size()>=3&&original.substr(0,3)=="\xef\xbb\xbf"){bom=original.substr(0,3);original.erase(0,3);}
    std::vector<Line> lines;size_t begin=0;while(begin<original.size()){size_t end=original.find_first_of("\r\n",begin);if(end==std::string::npos){lines.push_back({original.substr(begin),""});break;}size_t count=original[end]=='\r'&&end+1<original.size()&&original[end+1]=='\n'?2:1;lines.push_back({original.substr(begin,end-begin),original.substr(end,count)});begin=end+count;}
    std::string newline="\r\n";for(const auto& line:lines)if(!line.end.empty()){newline=line.end;break;}
    bool display=false;std::map<std::string,std::string> values;for(const auto& line:lines){auto text=trim(line.text);if(!text.empty()&&text.front()=='['&&text.back()==']')display=lower(text)=="[display]";else if(display&&!text.empty()&&text.front()!=';'&&text.front()!='#'){auto equal=text.find('=');if(equal!=std::string::npos)values[lower(trim(text.substr(0,equal)))]=trim(text.substr(equal+1));}}
    auto size=dimensions(monitor.work,dimension(values,"windowedwidth",1024),dimension(values,"windowedheight",768));
    std::vector<std::pair<std::string,std::string>> required={{"Fullscreen","false"},{"Benchmarked","true"},{"WindowedWidth",std::to_string(size.first)},{"WindowedHeight",std::to_string(size.second)},{"WindowedX",std::to_string(monitor.work.left+std::max(0L,(monitor.work.right-monitor.work.left-size.first-32)/2))},{"WindowedY",std::to_string(monitor.work.top+std::max(0L,(monitor.work.bottom-monitor.work.top-size.second-64)/2))}};
    std::set<std::string> found;size_t insertion=SIZE_MAX;display=false;for(size_t i=0;i<lines.size();i++){auto text=trim(lines[i].text);if(!text.empty()&&text.front()=='['&&text.back()==']'){display=lower(text)=="[display]";if(display&&insertion==SIZE_MAX)insertion=i+1;}else if(display&&!text.empty()&&text.front()!=';'&&text.front()!='#'){auto equal=text.find('=');if(equal==std::string::npos)continue;auto key=lower(trim(text.substr(0,equal)));for(const auto& value:required)if(key==lower(value.first)){found.insert(key);if(trim(text.substr(equal+1))!=value.second)lines[i].text=value.first+" = "+value.second;}}}
    std::vector<Line> missing;for(const auto& value:required)if(!found.count(lower(value.first)))missing.push_back({value.first+" = "+value.second,newline});
    if(!missing.empty()){if(insertion==SIZE_MAX){if(!lines.empty()&&lines.back().end.empty())lines.back().end=newline;lines.push_back({"[Display]",newline});insertion=lines.size();}else if(lines[insertion-1].end.empty())lines[insertion-1].end=newline;lines.insert(lines.begin()+insertion,missing.begin(),missing.end());}
    std::string result=bom;for(const auto& line:lines)result+=line.text+line.end;return bytes(result);
}
static bool running(){HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);require(snapshot!=INVALID_HANDLE_VALUE,"Could not check running games.");PROCESSENTRY32W info{};info.dwSize=sizeof info;bool result=false;if(Process32FirstW(snapshot,&info))do{if(!_wcsicmp(info.szExeFile,L"DungeonRunners.exe"))result=true;}while(Process32NextW(snapshot,&info));CloseHandle(snapshot);return result;}
void verify_game_display(){
    GameMonitor probeMonitor{L"check-small",{0,0,1920,1080},{0,0,1920,1040},true};Bytes original=bytes("[Display]\r\nFullscreen=true\r\nWindowedWidth=5120\r\nWindowedHeight=1440\r\n[General]\r\nSound=false\r\n");
    Bytes applied=profile(original,probeMonitor);std::string text=string(applied);require(text.find("WindowedWidth = 1888")!=std::string::npos&&text.find("WindowedHeight = 976")!=std::string::npos&&text.find("Fullscreen = false")!=std::string::npos&&text.find("[General]\r\nSound=false\r\n")!=std::string::npos,"Native monitor sizing or setting preservation failed.");require(profile(applied,probeMonitor)==applied,"Native monitor settings are not idempotent.");
    probeMonitor.work={-1920,-240,0,800};text=string(profile(original,probeMonitor));require(text.find("WindowedX = -1920")!=std::string::npos&&text.find("WindowedY = -240")!=std::string::npos,"Native negative monitor coordinates failed.");
}
static void prepare(const Path& root,const GameMonitor& monitor){
    if(running())return;installation_lock(root,[&]{Path path=under(root,"config/User.cfg");Bytes previous=exists(path)?read(path,1048576):Bytes{},applied=profile(previous,monitor);if(applied==previous||running())return;
        if(exists(path)){GUID id;require(SUCCEEDED(CoCreateGuid(&id)),"Cannot preserve game display settings.");Path backup=under(root,".dr-client/backups/game-display-"+hex(reinterpret_cast<unsigned char*>(&id),sizeof id)+".cfg");atomic_write(backup,previous);require(read(backup,1048576)==previous,"Game display backup failed.");}
        require((exists(path)?read(path,1048576):Bytes{})==previous,"Game display settings changed. Select Play to retry.");check_cancelled();atomic_write(path,applied);
    });
}
struct GameWindow{DWORD process;HWND window=nullptr;};
static BOOL CALLBACK window_callback(HWND window,LPARAM parameter){auto state=reinterpret_cast<GameWindow*>(parameter);DWORD owner=0;GetWindowThreadProcessId(window,&owner);if(owner!=state->process||!IsWindowVisible(window))return TRUE;wchar_t name[64]{};GetClassNameW(window,name,64);if(wcscmp(name,L"DRMainWindow"))return TRUE;state->window=window;return FALSE;}
static GameMonitor resolve(const std::vector<GameMonitor>& monitors,const std::string& selection){require(!monitors.empty(),"No game monitor is available.");for(const auto& monitor:monitors)if(utf8(monitor.id)==selection)return monitor;for(const auto& monitor:monitors)if(monitor.primary)return monitor;return monitors.front();}
void launch_game(const Path& root){
    std::string selection=selected_monitor();if(selection.empty()){start(under(root,"DungeonRunners.exe"),{L"ran_from_launcher"},root,false);return;}
    auto monitor=resolve(game_monitors(),selection);prepare(root,monitor);Path executable=under(root,"DungeonRunners.exe"),command=quote(executable)+L" ran_from_launcher";
    STARTUPINFOW startup{};startup.cb=sizeof startup;PROCESS_INFORMATION process{};require(CreateProcessW(executable.c_str(),&command[0],nullptr,nullptr,FALSE,0,nullptr,root.c_str(),&startup,&process),"Could not start the game.");CloseHandle(process.hThread);
    try{DWORD begin=GetTickCount(),settled=0;while(GetTickCount()-begin<20000){require(WaitForSingleObject(process.hProcess,0)==WAIT_TIMEOUT,"The game closed before its monitor could be selected.");GameWindow window{process.dwProcessId};EnumWindows(window_callback,reinterpret_cast<LPARAM>(&window));if(window.window){Coordinates coordinates;monitor=resolve(game_monitors(),selection);RECT rect{};if(GetWindowRect(window.window,&rect)&&rect.right-rect.left>=100&&rect.bottom-rect.top>=100){if(rect.left>=monitor.work.left&&rect.top>=monitor.work.top&&rect.right<=monitor.work.right&&rect.bottom<=monitor.work.bottom&&!IsZoomed(window.window)){if(!settled)settled=GetTickCount();if(GetTickCount()-settled>=500){CloseHandle(process.hProcess);return;}}else{settled=0;DWORD owner=0;GetWindowThreadProcessId(window.window,&owner);require(owner==process.dwProcessId,"The game window is no longer available.");int width=std::min(rect.right-rect.left,monitor.work.right-monitor.work.left),height=std::min(rect.bottom-rect.top,monitor.work.bottom-monitor.work.top);if(IsZoomed(window.window))ShowWindowAsync(window.window,SW_RESTORE);require(SetWindowPos(window.window,nullptr,monitor.work.left+(monitor.work.right-monitor.work.left-width)/2,monitor.work.top+(monitor.work.bottom-monitor.work.top-height)/2,width,height,SWP_NOZORDER|SWP_NOACTIVATE|SWP_ASYNCWINDOWPOS)!=0,"Could not select the game monitor.");}}}Sleep(100);}throw std::runtime_error("The game is open, but its monitor could not be selected. Retry Play.");}catch(...){CloseHandle(process.hProcess);throw;}
}
}
