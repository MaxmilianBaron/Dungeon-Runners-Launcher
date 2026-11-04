#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
static LRESULT CALLBACK procedure(HWND window,UINT message,WPARAM wp,LPARAM lp){if(message==WM_CLOSE){DestroyWindow(window);return 0;}return DefWindowProcW(window,message,wp,lp);}
int main(void){
    WNDCLASSW cls={0};cls.lpfnWndProc=procedure;cls.hInstance=GetModuleHandleW(NULL);cls.lpszClassName=L"DRMainWindow";if(!RegisterClassW(&cls))return 1;
    HWND window=CreateWindowExW(WS_EX_LAYERED|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,cls.lpszClassName,L"Dungeon Runners monitor check",WS_OVERLAPPEDWINDOW,0,0,5120,1440,NULL,NULL,cls.hInstance,NULL);if(!window)return 2;
    SetLayeredWindowAttributes(window,0,0,LWA_ALPHA);ShowWindow(window,SW_SHOWNOACTIVATE);ShowWindow(window,SW_SHOWNOACTIVATE);
    char path[80];sprintf_s(path,sizeof path,"state-%lu.txt",GetCurrentProcessId());DWORD start=GetTickCount();
    while(IsWindow(window)&&GetTickCount()-start<30000&&GetFileAttributesA("stop")==INVALID_FILE_ATTRIBUTES){
        MSG message;while(PeekMessageW(&message,NULL,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
        typedef HANDLE(WINAPI* Change)(HANDLE);Change change=(Change)GetProcAddress(GetModuleHandleW(L"user32.dll"),"SetThreadDpiAwarenessContext");HANDLE previous=change?change((HANDLE)-4):NULL;
        RECT rect;if(GetWindowRect(window,&rect)){FILE* file=NULL;fopen_s(&file,path,"w");if(file){fprintf(file,"%lu %ld %ld %ld %ld",GetCurrentProcessId(),rect.left,rect.top,rect.right-rect.left,rect.bottom-rect.top);fclose(file);}}
        if(change&&previous)change(previous);Sleep(100);
    }
    if(IsWindow(window))DestroyWindow(window);return 0;
}
#else
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/Xatom.h>
#include <stdio.h>
#include <time.h>
#include <unistd.h>
int main(void){
    Display* display=XOpenDisplay(NULL);if(!display)return 1;Window root=DefaultRootWindow(display),window=XCreateSimpleWindow(display,root,0,0,5120,1440,0,0,0);
    XClassHint hint={"dungeonrunners.exe","dungeonrunners.exe"};XSetClassHint(display,window,&hint);unsigned long pid=getpid();Atom atom=XInternAtom(display,"_NET_WM_PID",False);XChangeProperty(display,window,atom,XA_CARDINAL,32,PropModeReplace,(unsigned char*)&pid,1);XStoreName(display,window,"Dungeon Runners monitor check");XMapWindow(display,window);XFlush(display);
    char path[80];snprintf(path,sizeof path,"state-%lu.txt",pid);time_t start=time(NULL);
    while(time(NULL)-start<30&&access("stop",F_OK)!=0){
        Window parent,child;int x,y,translatedX,translatedY;unsigned int width,height,border,depth;
        if(XGetGeometry(display,window,&parent,&x,&y,&width,&height,&border,&depth)&&XTranslateCoordinates(display,window,root,0,0,&translatedX,&translatedY,&child)){FILE* file=fopen(path,"w");if(file){fprintf(file,"%lu %d %d %u %u",pid,translatedX,translatedY,width,height);fclose(file);}}
        XFlush(display);usleep(100000);
    }
    XDestroyWindow(display,window);XCloseDisplay(display);return 0;
}
#endif
