#include "launcher.h"
#include <commctrl.h>
#include <gdiplus.h>

namespace dr {
static HWND window=nullptr,primary=nullptr,update=nullptr,repair=nullptr,folder=nullptr,browse=nullptr,cancel=nullptr;
static HFONT font=nullptr;
static bool busy=false,closing=false,installed=false,smoke_test=false;
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
    for(HWND item:{primary,update,repair,folder,browse})EnableWindow(item,!busy);EnableWindow(repair,!busy&&installed);ShowWindow(cancel,busy?SW_SHOW:SW_HIDE);
}
static DWORD WINAPI work(void* parameter) {
    int action=static_cast<int>(reinterpret_cast<intptr_t>(parameter));Done result{};
    DWORD last=0;
    auto progress=[&](const std::string& phase,uint64_t done,uint64_t size){DWORD now=GetTickCount();if(size&&done<size&&now-last<100)return;last=now;auto state=new State{phase,done,size};if(!PostMessageW(window,status_message,0,reinterpret_cast<LPARAM>(state)))delete state;};
    try {
        if(action==0){manifest(download(feed,131072));}
        else if(action==1&&installed){play(root,progress);result.close=true;}
        else {
            auto signed_bytes=download(feed,131072);install(root,signed_bytes,progress);entry_points(root);requirements(root,progress);
            if(action==2)result.close=update_launcher(root,progress);
        }
    }catch(const std::exception& error){result.error=error.what();}
    auto done=new Done(result);if(!PostMessageW(window,done_message,0,reinterpret_cast<LPARAM>(done)))delete done;return 0;
}
static void run(int action) {
    if(busy)return;wchar_t selected[MAX_PATH];GetWindowTextW(folder,selected,MAX_PATH);try{root=game_root(selected);refresh();}catch(const std::exception& error){detail=wide(error.what());InvalidateRect(window,nullptr,FALSE);return;}
    busy=true;InterlockedExchange(&cancelled,0);completed=total=0;status=action==1&&installed?L"Preparing game":L"Checking for updates";detail=L"Connecting to the release service...";refresh();InvalidateRect(window,nullptr,FALSE);
    HANDLE thread=CreateThread(nullptr,0,work,reinterpret_cast<void*>(static_cast<intptr_t>(action)),0,nullptr);if(thread)CloseHandle(thread);else{busy=false;status=L"Could not finish";detail=L"Cannot start the operation.";refresh();}
}
static void layout(HWND hwnd) {
    RECT r;GetClientRect(hwnd,&r);int width=r.right,height=r.bottom,left=std::max(18,width/8),content=width-left*2;
    MoveWindow(folder,left,height-174,content-86,25,TRUE);MoveWindow(browse,left+content-78,height-174,78,25,TRUE);
    int middle=width/2;MoveWindow(primary,middle-180,height-49,126,32,TRUE);MoveWindow(update,middle-44,height-49,108,32,TRUE);MoveWindow(repair,middle+74,height-49,106,32,TRUE);MoveWindow(cancel,left+content-80,height-127,80,27,TRUE);InvalidateRect(hwnd,nullptr,FALSE);
}
static void text(HDC dc,const std::wstring& value,RECT rect,COLORREF color,UINT format=DT_LEFT|DT_WORDBREAK) {SetBkMode(dc,TRANSPARENT);SetTextColor(dc,color);SelectObject(dc,font);DrawTextW(dc,value.c_str(),static_cast<int>(value.size()),&rect,format);}
static LRESULT CALLBACK procedure(HWND hwnd,UINT message,WPARAM wp,LPARAM lp) {
    switch(message) {
        case WM_SIZE:layout(hwnd);return 0;
        case WM_GETMINMAXINFO:{auto limits=reinterpret_cast<MINMAXINFO*>(lp);limits->ptMinTrackSize={600,520};return 0;}
        case WM_ERASEBKGND:return 1;
        case WM_PAINT:{PAINTSTRUCT paint;HDC dc=BeginPaint(hwnd,&paint);RECT r;GetClientRect(hwnd,&r);HDC buffer=CreateCompatibleDC(dc);HBITMAP surface=CreateCompatibleBitmap(dc,r.right,r.bottom);HGDIOBJ old=SelectObject(buffer,surface);
            {Gdiplus::Graphics graphics(buffer);graphics.SetInterpolationMode(Gdiplus::InterpolationModeHighQualityBicubic);graphics.Clear(Gdiplus::Color(255,16,14,12));if(background)graphics.DrawImage(background.get(),0,0,r.right,r.bottom);int left=std::max<int>(18,r.right/8),width=r.right-2*left;
            Gdiplus::Pen border(Gdiplus::Color(255,158,121,62),3);graphics.DrawRectangle(&border,5,5,r.right-11,r.bottom-11);if(logo)graphics.DrawImage(logo.get(),(r.right-300)/2,25,300,75);
            int top=122,height=r.bottom-318;if(artwork&&height>0) {Gdiplus::SolidBrush black(Gdiplus::Color(255,0,0,0));graphics.FillRectangle(&black,left,top,width,height);float image_height=static_cast<float>(std::min(height,width*548/1024));graphics.DrawImage(artwork.get(),Gdiplus::RectF(static_cast<float>(left),top+(height-image_height)/2,static_cast<float>(width),image_height),0.0f,224.0f,1024.0f,548.0f,Gdiplus::UnitPixel);graphics.DrawRectangle(&border,left,top,width,height);}
            Gdiplus::SolidBrush dark(Gdiplus::Color(238,13,11,8));graphics.FillRectangle(&dark,left,r.bottom-139,width,80);Gdiplus::Pen edge(Gdiplus::Color(255,89,68,37));graphics.DrawRectangle(&edge,left,r.bottom-139,width,80);
            RECT title={left+12,r.bottom-132,left+width-94,r.bottom-109};text(buffer,status,title,RGB(243,204,132));RECT explanation={left+12,r.bottom-105,left+width-12,r.bottom-70};text(buffer,detail,explanation,RGB(237,217,177));
            if(busy&&total){Gdiplus::SolidBrush gold(Gdiplus::Color(255,220,183,94));graphics.FillRectangle(&gold,left+12,r.bottom-67,static_cast<int>((width-24)*std::min(completed,total)/total),4);}}
            BitBlt(dc,0,0,r.right,r.bottom,buffer,0,0,SRCCOPY);SelectObject(buffer,old);DeleteObject(surface);DeleteDC(buffer);EndPaint(hwnd,&paint);return 0;}
        case WM_DRAWITEM:{auto item=reinterpret_cast<DRAWITEMSTRUCT*>(lp);if(item->CtlType!=ODT_BUTTON)break;RECT r=item->rcItem;HBRUSH fill=CreateSolidBrush((item->itemState&ODS_SELECTED)?RGB(103,35,14):RGB(49,13,5));FillRect(item->hDC,&r,fill);DeleteObject(fill);HBRUSH frame=CreateSolidBrush(RGB(160,115,51));FrameRect(item->hDC,&r,frame);DeleteObject(frame);wchar_t label[80];GetWindowTextW(item->hwndItem,label,80);text(item->hDC,label,r,(item->itemState&ODS_DISABLED)?RGB(126,107,75):RGB(241,201,122),DT_CENTER|DT_VCENTER|DT_SINGLELINE);return TRUE;}
        case WM_COMMAND:
            if(HIWORD(wp)==BN_CLICKED){switch(LOWORD(wp)){case 1:run(1);break;case 2:run(2);break;case 3:run(3);break;case 4:{BROWSEINFOW info{};info.hwndOwner=hwnd;info.lpszTitle=L"Choose the Dungeon Runners game folder";info.ulFlags=BIF_RETURNONLYFSDIRS|BIF_NEWDIALOGSTYLE;LPITEMIDLIST item=SHBrowseForFolderW(&info);wchar_t selected[MAX_PATH];if(item&&SHGetPathFromIDListW(item,selected)){try{root=game_root(selected);SetWindowTextW(folder,selected);refresh();}catch(const std::exception& error){detail=wide(error.what());InvalidateRect(hwnd,nullptr,FALSE);}}CoTaskMemFree(item);break;}case 5:InterlockedExchange(&cancelled,1);detail=L"Cancelling...";InvalidateRect(hwnd,nullptr,FALSE);break;}}return 0;
        case status_message:{std::unique_ptr<State> state(reinterpret_cast<State*>(lp));status=wide(state->text);completed=state->done;total=state->size;detail=total?std::to_wstring(completed/1048576)+L" / "+std::to_wstring(total/1048576)+L" MiB":L"Please keep the launcher open.";EnableWindow(cancel,state->text!="Installing");InvalidateRect(hwnd,nullptr,FALSE);return 0;}
        case done_message:{std::unique_ptr<Done> done(reinterpret_cast<Done*>(lp));busy=false;status=done->error.empty()?L"Ready":L"Could not finish";detail=wide(done->error);refresh();InvalidateRect(hwnd,nullptr,FALSE);if(closing||done->close)DestroyWindow(hwnd);return 0;}
        case WM_TIMER:if(smoke_test)DestroyWindow(hwnd);return 0;
        case WM_CLOSE:if(busy){closing=true;InterlockedExchange(&cancelled,1);detail=L"Finishing the current operation...";InvalidateRect(hwnd,nullptr,FALSE);}else DestroyWindow(hwnd);return 0;
        case WM_DESTROY:PostQuitMessage(0);return 0;
    }
    return DefWindowProcW(hwnd,message,wp,lp);
}
int legacy_ui(bool smoke) {
    smoke_test=smoke;CoInitialize(nullptr);Gdiplus::GdiplusStartupInput input;ULONG_PTR graphics;require(Gdiplus::GdiplusStartup(&graphics,&input,nullptr)==Gdiplus::Ok,"Cannot initialize the launcher interface.");
    background=bitmap(201);artwork=bitmap(202);logo=bitmap(203);font=CreateFontW(-17,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Georgia");
    root=under(local_data(),"Dungeon Runners");Path preference=under(local_data(),"Dungeon Runners Launcher/folder.txt");try{if(exists(preference))root=absolute(wide(string(read(preference,4096))));}catch(...){}
    if(exists(under(directory(self()),"DungeonRunners.exe")))root=directory(self());
    WNDCLASSW cls{};cls.lpfnWndProc=procedure;cls.hInstance=GetModuleHandleW(nullptr);cls.lpszClassName=L"DungeonRunnersWindowsLauncher";cls.hCursor=LoadCursor(nullptr,IDC_ARROW);cls.hIcon=LoadIconW(cls.hInstance,MAKEINTRESOURCEW(1));require(RegisterClassW(&cls)!=0,"Cannot register launcher window.");
    RECT desktop;SystemParametersInfoW(SPI_GETWORKAREA,0,&desktop,0);int width=std::min<int>(960,desktop.right-desktop.left),height=std::min<int>(780,desktop.bottom-desktop.top);
    window=CreateWindowExW(0,cls.lpszClassName,L"Dungeon Runners - Reborn",WS_OVERLAPPEDWINDOW,desktop.left+(desktop.right-desktop.left-width)/2,desktop.top+(desktop.bottom-desktop.top-height)/2,width,height,nullptr,nullptr,cls.hInstance,nullptr);require(window!=nullptr,"Cannot open launcher window.");
    auto button=[&](int id,const wchar_t* label){return CreateWindowExW(0,L"BUTTON",label,WS_CHILD|WS_VISIBLE|WS_TABSTOP|BS_OWNERDRAW,0,0,100,30,window,reinterpret_cast<HMENU>(static_cast<intptr_t>(id)),cls.hInstance,nullptr);};
    primary=button(1,L"Install game");update=button(2,L"Update");repair=button(3,L"Repair");browse=button(4,L"Browse");cancel=button(5,L"Cancel");folder=CreateWindowExW(WS_EX_CLIENTEDGE,L"EDIT",root.c_str(),WS_CHILD|WS_VISIBLE|WS_TABSTOP|ES_AUTOHSCROLL,0,0,100,25,window,nullptr,cls.hInstance,nullptr);SendMessageW(folder,WM_SETFONT,reinterpret_cast<WPARAM>(font),TRUE);
    layout(window);refresh();ShowWindow(window,SW_SHOW);UpdateWindow(window);if(smoke){status=L"Ready";detail=L"";SetTimer(window,1,1000,nullptr);}else run(0);
    MSG message;while(GetMessageW(&message,nullptr,0,0)>0){if(message.message==WM_KEYDOWN&&message.wParam==VK_ESCAPE){SendMessageW(window,WM_CLOSE,0,0);continue;}if(!IsDialogMessageW(window,&message)){TranslateMessage(&message);DispatchMessageW(&message);}}
    background.reset();artwork.reset();logo.reset();DeleteObject(font);Gdiplus::GdiplusShutdown(graphics);CoUninitialize();return 0;
}
}
