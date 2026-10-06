#include "launcher.h"
#include <commctrl.h>
#include <gdiplus.h>

namespace dr {
static HWND window=nullptr,primary=nullptr,update=nullptr,repair=nullptr,folder=nullptr,browse=nullptr,cancel=nullptr,addons=nullptr,addonInstall=nullptr,addonRemove=nullptr,addonClose=nullptr,website=nullptr,armory=nullptr,discord=nullptr;
static HFONT font=nullptr;
static HBRUSH fieldBrush=nullptr;
static bool busy=false,closing=false,installed=false,smoke_test=false,addonsOpen=false,addonsInstalled=false;
static Path root;
static std::wstring status=L"Checking for updates",detail=L"Connecting to the release service...";
static uint64_t completed=0,total=0;
static std::unique_ptr<Gdiplus::Bitmap> background,artwork,logo;
static const UINT status_message=WM_APP+1,done_message=WM_APP+2;
struct State { std::string text;uint64_t done,size; };
struct Done {std::string error;bool close;};
static std::unique_ptr<Gdiplus::Bitmap> bitmap(int id) {
    auto data=resource(id);HGLOBAL memory=GlobalAlloc(GMEM_MOVEABLE,data.size());require(memory!=nullptr,"Cannot load artwork.");void* target=GlobalLock(memory);memcpy(target,data.data(),data.size());GlobalUnlock(memory);IStream* stream=nullptr;require(SUCCEEDED(CreateStreamOnHGlobal(memory,TRUE,&stream)),"Cannot load artwork stream.");
    Gdiplus::Bitmap image(stream);auto copy=std::unique_ptr<Gdiplus::Bitmap>(image.Clone(0,0,image.GetWidth(),image.GetHeight(),PixelFormat32bppARGB));stream->Release();return copy;
}
static void refresh() {
    installed=exists(under(root,"DungeonRunners.exe"));SetWindowTextW(primary,installed?L"Play":L"Install game");
    addonsInstalled=exists(under(root,"Addons/Runtime/Addons.dll"));SetWindowTextW(addonInstall,addonsInstalled?L"Update addons":L"Install addons");
    for(HWND item:{primary,update,repair,folder,browse,addons,addonInstall,addonRemove})EnableWindow(item,!busy);EnableWindow(repair,!busy&&installed);EnableWindow(addonInstall,!busy&&installed);EnableWindow(addonRemove,!busy&&addonsInstalled);ShowWindow(cancel,busy?SW_SHOW:SW_HIDE);
    ShowWindow(addonInstall,addonsOpen?SW_SHOW:SW_HIDE);ShowWindow(addonRemove,addonsOpen&&addonsInstalled?SW_SHOW:SW_HIDE);ShowWindow(addonClose,addonsOpen?SW_SHOW:SW_HIDE);
}
static DWORD WINAPI work(void* parameter) {
    int action=static_cast<int>(reinterpret_cast<intptr_t>(parameter));Done result{};
    DWORD last=0;
    auto progress=[&](const std::string& phase,uint64_t done,uint64_t size){DWORD now=GetTickCount();if(size&&done<size&&now-last<100)return;last=now;auto state=new State{phase,done,size};if(!PostMessageW(window,status_message,0,reinterpret_cast<LPARAM>(state)))delete state;};
    try {
        if(action==0){manifest(download(feed,131072));}
        else if(action==4){install_addons(root,progress);}
        else if(action==5){remove_addons(root,progress);}
        else if(action==1&&installed){play(root,progress);result.close=true;}
        else {
            auto signed_bytes=download(feed,131072);install(root,signed_bytes,progress);entry_points(root);requirements(root,progress);
            if(exists(under(root,"Addons/Runtime/Addons.dll")))install_addons(root,progress);
            if(action==2)result.close=update_launcher(root,progress);
        }
    }catch(const std::exception& error){result.error=error.what();}
    auto done=new Done(result);if(!PostMessageW(window,done_message,0,reinterpret_cast<LPARAM>(done)))delete done;return 0;
}
static void run(int action) {
    if(busy)return;wchar_t selected[MAX_PATH];GetWindowTextW(folder,selected,MAX_PATH);try{root=game_root(selected);refresh();}catch(const std::exception& error){detail=wide(error.what());InvalidateRect(window,nullptr,FALSE);return;}
    busy=true;InterlockedExchange(&cancelled,0);completed=total=0;status=action>=4?L"Checking addons":action==1&&installed?L"Preparing game":L"Checking for updates";detail=L"Connecting to the release service...";refresh();InvalidateRect(window,nullptr,FALSE);
    HANDLE thread=CreateThread(nullptr,0,work,reinterpret_cast<void*>(static_cast<intptr_t>(action)),0,nullptr);if(thread)CloseHandle(thread);else{busy=false;status=L"Could not finish";detail=L"Cannot start the operation.";refresh();}
}
static void layout(HWND hwnd) {
    RECT r;GetClientRect(hwnd,&r);int width=r.right,height=r.bottom,left=std::max(18,width/8),content=width-left*2;
    MoveWindow(folder,left,height-174,content-86,25,TRUE);MoveWindow(browse,left+content-78,height-174,78,25,TRUE);
    int middle=width/2;MoveWindow(primary,middle-227,height-49,126,32,TRUE);MoveWindow(update,middle-91,height-49,108,32,TRUE);MoveWindow(addons,middle+27,height-49,108,32,TRUE);MoveWindow(repair,middle+145,height-46,82,28,TRUE);MoveWindow(cancel,left+content-80,height-127,80,27,TRUE);
    MoveWindow(addonClose,left+content-92,142,70,28,TRUE);MoveWindow(addonInstall,left+22,height-244,138,32,TRUE);MoveWindow(addonRemove,left+172,height-244,144,32,TRUE);
    const int linksTop=width<760?86:54;MoveWindow(website,left,linksTop,70,28,TRUE);MoveWindow(armory,left+76,linksTop,70,28,TRUE);MoveWindow(discord,left+content-70,linksTop,70,28,TRUE);InvalidateRect(hwnd,nullptr,FALSE);
}
static void text(HDC dc,const std::wstring& value,RECT rect,COLORREF color,UINT format=DT_LEFT|DT_WORDBREAK) {SetBkMode(dc,TRANSPARENT);SetTextColor(dc,color);SelectObject(dc,font);DrawTextW(dc,value.c_str(),static_cast<int>(value.size()),&rect,format);}
static LRESULT CALLBACK procedure(HWND hwnd,UINT message,WPARAM wp,LPARAM lp) {
    switch(message) {
        case WM_SIZE:layout(hwnd);return 0;
        case WM_GETMINMAXINFO:{auto limits=reinterpret_cast<MINMAXINFO*>(lp);limits->ptMinTrackSize={600,520};return 0;}
        case WM_ERASEBKGND:return 1;
        case WM_PAINT:case WM_PRINTCLIENT:{PAINTSTRUCT paint{};HDC dc=message==WM_PAINT?BeginPaint(hwnd,&paint):reinterpret_cast<HDC>(wp);RECT r;GetClientRect(hwnd,&r);HDC buffer=CreateCompatibleDC(dc);HBITMAP surface=CreateCompatibleBitmap(dc,r.right,r.bottom);HGDIOBJ old=SelectObject(buffer,surface);
            {Gdiplus::Graphics graphics(buffer);graphics.SetInterpolationMode(Gdiplus::InterpolationModeHighQualityBicubic);graphics.Clear(Gdiplus::Color(255,16,14,12));if(background)graphics.DrawImage(background.get(),0,0,r.right,r.bottom);int left=std::max<int>(18,r.right/8),width=r.right-2*left;
            Gdiplus::Pen border(Gdiplus::Color(255,158,121,62),3);graphics.DrawRectangle(&border,5,5,r.right-11,r.bottom-11);if(logo){int logoWidth=r.right<760?260:300;graphics.DrawImage(logo.get(),(r.right-logoWidth)/2,r.right<760?12:25,logoWidth,logoWidth/4);}
            int top=122,height=r.bottom-318;if(artwork&&height>0) {Gdiplus::SolidBrush black(Gdiplus::Color(255,0,0,0));graphics.FillRectangle(&black,left,top,width,height);float image_height=static_cast<float>(std::min(height,width*548/1024));graphics.DrawImage(artwork.get(),Gdiplus::RectF(static_cast<float>(left),top+(height-image_height)/2,static_cast<float>(width),image_height),0.0f,224.0f,1024.0f,548.0f,Gdiplus::UnitPixel);graphics.DrawRectangle(&border,left,top,width,height);}
            if(addonsOpen&&height>0){Gdiplus::SolidBrush panel(Gdiplus::Color(245,17,15,12));graphics.FillRectangle(&panel,left+2,top+2,width-4,height-4);RECT heading={left+22,top+20,left+width-108,top+56};text(buffer,L"Dungeon Runners Addons",heading,RGB(243,204,132));RECT description={left+22,top+std::max(65,(height-65)/2),left+width-22,top+height-70};text(buffer,installed?L"Install or uninstall the addon collection.\nSettings and history are kept.\nIn the game, open ESC \x2192 Addons.":L"Install the game first to manage addons.",description,RGB(237,217,177));}
            Gdiplus::SolidBrush dark(Gdiplus::Color(238,13,11,8));graphics.FillRectangle(&dark,left,r.bottom-139,width,80);Gdiplus::Pen edge(Gdiplus::Color(255,89,68,37));graphics.DrawRectangle(&edge,left,r.bottom-139,width,80);
            RECT title={left+12,r.bottom-132,left+width-94,r.bottom-109};text(buffer,status,title,RGB(243,204,132));RECT explanation={left+12,r.bottom-105,left+width-12,r.bottom-70};text(buffer,detail,explanation,RGB(237,217,177));
            if(busy&&total){Gdiplus::SolidBrush gold(Gdiplus::Color(255,220,183,94));graphics.FillRectangle(&gold,left+12,r.bottom-67,static_cast<int>((width-24)*std::min(completed,total)/total),4);}}
            BitBlt(dc,0,0,r.right,r.bottom,buffer,0,0,SRCCOPY);SelectObject(buffer,old);DeleteObject(surface);DeleteDC(buffer);if(message==WM_PAINT)EndPaint(hwnd,&paint);return 0;}
        case WM_CTLCOLOREDIT:{HDC dc=reinterpret_cast<HDC>(wp);SetTextColor(dc,RGB(239,230,207));SetBkColor(dc,RGB(24,20,15));return reinterpret_cast<LRESULT>(fieldBrush);}
        case WM_DRAWITEM:{auto item=reinterpret_cast<DRAWITEMSTRUCT*>(lp);if(item->CtlType!=ODT_BUTTON)break;RECT r=item->rcItem;HBRUSH fill=CreateSolidBrush((item->itemState&ODS_SELECTED)?RGB(103,35,14):RGB(49,13,5));FillRect(item->hDC,&r,fill);DeleteObject(fill);HBRUSH frame=CreateSolidBrush(RGB(160,115,51));FrameRect(item->hDC,&r,frame);DeleteObject(frame);wchar_t label[80];GetWindowTextW(item->hwndItem,label,80);text(item->hDC,label,r,(item->itemState&ODS_DISABLED)?RGB(126,107,75):RGB(241,201,122),DT_CENTER|DT_VCENTER|DT_SINGLELINE);return TRUE;}
        case WM_COMMAND:
            if(HIWORD(wp)==BN_CLICKED){switch(LOWORD(wp)){case 1:run(1);break;case 2:run(2);break;case 3:run(3);break;case 4:{BROWSEINFOW info{};info.hwndOwner=hwnd;info.lpszTitle=L"Choose the Dungeon Runners game folder";info.ulFlags=BIF_RETURNONLYFSDIRS|BIF_NEWDIALOGSTYLE;LPITEMIDLIST item=SHBrowseForFolderW(&info);wchar_t selected[MAX_PATH];if(item&&SHGetPathFromIDListW(item,selected)){try{root=game_root(selected);SetWindowTextW(folder,selected);refresh();}catch(const std::exception& error){detail=wide(error.what());InvalidateRect(hwnd,nullptr,FALSE);}}CoTaskMemFree(item);break;}case 5:InterlockedExchange(&cancelled,1);detail=L"Cancelling...";InvalidateRect(hwnd,nullptr,FALSE);break;case 6:addonsOpen=!addonsOpen;refresh();InvalidateRect(hwnd,nullptr,FALSE);break;case 7:run(4);break;case 8:run(5);break;case 9:addonsOpen=false;refresh();InvalidateRect(hwnd,nullptr,FALSE);break;case 10:ShellExecuteW(hwnd,L"open",L"https://www.dungeonrunnersreborn.com/",nullptr,nullptr,SW_SHOWNORMAL);break;case 11:ShellExecuteW(hwnd,L"open",L"https://dr-armory.com/",nullptr,nullptr,SW_SHOWNORMAL);break;case 12:ShellExecuteW(hwnd,L"open",L"https://discord.gg/hpXWhuS9Yj",nullptr,nullptr,SW_SHOWNORMAL);break;}}return 0;
        case status_message:{std::unique_ptr<State> state(reinterpret_cast<State*>(lp));status=wide(state->text);completed=state->done;total=state->size;detail=total?std::to_wstring(completed/1048576)+L" / "+std::to_wstring(total/1048576)+L" MiB":L"Please keep the launcher open.";EnableWindow(cancel,state->text!="Installing");InvalidateRect(hwnd,nullptr,FALSE);return 0;}
        case done_message:{std::unique_ptr<Done> done(reinterpret_cast<Done*>(lp));busy=false;status=done->error.empty()?L"Ready":L"Could not finish";detail=wide(done->error);refresh();InvalidateRect(hwnd,nullptr,FALSE);if(closing||done->close)DestroyWindow(hwnd);return 0;}
        case WM_TIMER:if(smoke_test)DestroyWindow(hwnd);return 0;
        case WM_CLOSE:if(busy){closing=true;InterlockedExchange(&cancelled,1);detail=L"Finishing the current operation...";InvalidateRect(hwnd,nullptr,FALSE);}else DestroyWindow(hwnd);return 0;
        case WM_DESTROY:PostQuitMessage(0);return 0;
    }
    return DefWindowProcW(hwnd,message,wp,lp);
}
static void preview(const Path& destination) {
    RECT rect;GetClientRect(window,&rect);HDC dc=GetDC(window),memory=CreateCompatibleDC(dc);HBITMAP surface=CreateCompatibleBitmap(dc,rect.right,rect.bottom);HGDIOBJ old=SelectObject(memory,surface);
    SendMessageW(window,WM_PRINTCLIENT,reinterpret_cast<WPARAM>(memory),PRF_CLIENT);
    for(HWND child=GetWindow(window,GW_CHILD);child;child=GetWindow(child,GW_HWNDNEXT))if(GetWindowLongW(child,GWL_STYLE)&WS_VISIBLE){RECT area;GetWindowRect(child,&area);MapWindowPoints(nullptr,window,reinterpret_cast<POINT*>(&area),2);int saved=SaveDC(memory);SetViewportOrgEx(memory,area.left,area.top,nullptr);SendMessageW(child,WM_PRINT,reinterpret_cast<WPARAM>(memory),PRF_CLIENT|PRF_NONCLIENT|PRF_ERASEBKGND);RestoreDC(memory,saved);}
    CLSID encoder;require(SUCCEEDED(CLSIDFromString(L"{557CF406-1A04-11D3-9A73-0000F81EF32E}",&encoder)),"Cannot initialize PNG encoder.");
    {Gdiplus::Bitmap output(surface,nullptr);require(output.Save(destination.c_str(),&encoder)==Gdiplus::Ok,"Cannot save launcher preview.");}
    SelectObject(memory,old);DeleteObject(surface);DeleteDC(memory);ReleaseDC(window,dc);
}
int legacy_ui(bool smoke,const Path& previews) {
    smoke_test=smoke;CoInitialize(nullptr);Gdiplus::GdiplusStartupInput input;ULONG_PTR graphics;require(Gdiplus::GdiplusStartup(&graphics,&input,nullptr)==Gdiplus::Ok,"Cannot initialize the launcher interface.");
    auto fontData=resource(204);DWORD fonts=0;HANDLE fontResource=AddFontMemResourceEx(fontData.data(),static_cast<DWORD>(fontData.size()),nullptr,&fonts);
    background=bitmap(201);artwork=bitmap(202);logo=bitmap(203);font=CreateFontW(-17,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,fontResource?L"Source Serif 4":L"Georgia");fieldBrush=CreateSolidBrush(RGB(24,20,15));
    root=under(local_data(),"Dungeon Runners");Path preference=under(local_data(),"Dungeon Runners Launcher/folder.txt");try{if(exists(preference))root=absolute(wide(string(read(preference,4096))));}catch(...){}
    if(exists(under(directory(self()),"DungeonRunners.exe")))root=directory(self());
    WNDCLASSW cls{};cls.lpfnWndProc=procedure;cls.hInstance=GetModuleHandleW(nullptr);cls.lpszClassName=L"DungeonRunnersWindowsLauncher";cls.hCursor=LoadCursor(nullptr,IDC_ARROW);cls.hIcon=LoadIconW(cls.hInstance,MAKEINTRESOURCEW(1));require(RegisterClassW(&cls)!=0,"Cannot register launcher window.");
    RECT desktop;SystemParametersInfoW(SPI_GETWORKAREA,0,&desktop,0);int width=std::min<int>(960,desktop.right-desktop.left),height=std::min<int>(780,desktop.bottom-desktop.top);
    window=CreateWindowExW(0,cls.lpszClassName,L"Dungeon Runners - Reborn",WS_OVERLAPPEDWINDOW,desktop.left+(desktop.right-desktop.left-width)/2,desktop.top+(desktop.bottom-desktop.top-height)/2,width,height,nullptr,nullptr,cls.hInstance,nullptr);require(window!=nullptr,"Cannot open launcher window.");
    auto button=[&](int id,const wchar_t* label){return CreateWindowExW(0,L"BUTTON",label,WS_CHILD|WS_VISIBLE|WS_TABSTOP|BS_OWNERDRAW,0,0,100,30,window,reinterpret_cast<HMENU>(static_cast<intptr_t>(id)),cls.hInstance,nullptr);};
    primary=button(1,L"Install game");update=button(2,L"Update");repair=button(3,L"Repair");browse=button(4,L"Browse");cancel=button(5,L"Cancel");addons=button(6,L"Addons");addonInstall=button(7,L"Install addons");addonRemove=button(8,L"Uninstall addons");addonClose=button(9,L"Close");website=button(10,L"Website");armory=button(11,L"Armory");discord=button(12,L"Discord");folder=CreateWindowExW(WS_EX_CLIENTEDGE,L"EDIT",root.c_str(),WS_CHILD|WS_VISIBLE|WS_TABSTOP|ES_AUTOHSCROLL,0,0,100,25,window,nullptr,cls.hInstance,nullptr);SendMessageW(folder,WM_SETFONT,reinterpret_cast<WPARAM>(font),TRUE);
    layout(window);refresh();
    if(!previews.empty()){folders(previews);status=L"Ready";detail=L"";preview(under(previews,"launcher.png"));addonsOpen=true;refresh();preview(under(previews,"addons.png"));SetWindowPos(window,nullptr,0,0,640,560,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE);preview(under(previews,"addons-small.png"));DestroyWindow(window);}
    else{ShowWindow(window,SW_SHOW);UpdateWindow(window);if(smoke){status=L"Ready";detail=L"";SetTimer(window,1,1000,nullptr);}else run(0);}
    MSG message;while(GetMessageW(&message,nullptr,0,0)>0){if(message.message==WM_KEYDOWN&&message.wParam==VK_ESCAPE){if(addonsOpen){addonsOpen=false;refresh();InvalidateRect(window,nullptr,FALSE);}else SendMessageW(window,WM_CLOSE,0,0);continue;}if(!IsDialogMessageW(window,&message)){TranslateMessage(&message);DispatchMessageW(&message);}}
    background.reset();artwork.reset();logo.reset();DeleteObject(font);DeleteObject(fieldBrush);if(fontResource)RemoveFontMemResourceEx(fontResource);Gdiplus::GdiplusShutdown(graphics);CoUninitialize();return 0;
}
}
