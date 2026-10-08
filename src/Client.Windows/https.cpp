#include "launcher.h"

namespace dr {
struct Url { std::string host, path; };
static Url split_url(const std::string& url) {
    require(url.size()<16384 && url.compare(0,8,"https://")==0 && url.find_first_of("\r\n\\# ") == std::string::npos && url.find('\0') == std::string::npos,"Invalid HTTPS address.");
    auto slash=url.find('/',8); require(slash!=std::string::npos,"Invalid HTTPS path.");
    Url result{url.substr(8,slash-8),url.substr(slash)};
    require(!result.host.empty() && result.host.find_first_not_of("abcdefghijklmnopqrstuvwxyz0123456789.-")==std::string::npos,"Invalid HTTPS host."); return result;
}
bool allowed_url(const std::string& url) {
    Url u=split_url(url);
    if (u.host=="release-assets.githubusercontent.com" || u.host=="objects.githubusercontent.com") return true;
    if (u.host=="github.com") return u.path.find("/MaxmilianBaron/Dungeon-Runners-Launcher/releases/")==0
        || u.path.find("/MaxmilianBaron/Dungeon-Runners-Addons/releases/")==0;
    if (u.host=="api.github.com") return u.path=="/repos/MaxmilianBaron/Dungeon-Runners-Launcher/releases/latest" || u.path=="/repos/MaxmilianBaron/Dungeon-Runners-Addons/releases/latest";
    return url=="https://download.microsoft.com/download/8/4/a/84a35bf1-dafe-4ae8-82af-ad2ae20b6b14/directx_Jun2010_redist.exe";
}
class Tls {
    SOCKET socket_=INVALID_SOCKET;
    mbedtls_ssl_context ssl{};
    mbedtls_ssl_config config{};
    mbedtls_x509_crt ca{};
    mbedtls_entropy_context entropy{};
    mbedtls_ctr_drbg_context rng{};
    static int send_bytes(void* data,const unsigned char* buffer,size_t size) { int n=::send(*static_cast<SOCKET*>(data),reinterpret_cast<const char*>(buffer),static_cast<int>(std::min<size_t>(size,INT_MAX)),0); return n==SOCKET_ERROR?MBEDTLS_ERR_SSL_INTERNAL_ERROR:n; }
    static int receive_bytes(void* data,unsigned char* buffer,size_t size) { int n=recv(*static_cast<SOCKET*>(data),reinterpret_cast<char*>(buffer),static_cast<int>(std::min<size_t>(size,INT_MAX)),0); return n==SOCKET_ERROR?MBEDTLS_ERR_SSL_TIMEOUT:n; }
public:
    Tls() { mbedtls_ssl_init(&ssl); mbedtls_ssl_config_init(&config); mbedtls_x509_crt_init(&ca); mbedtls_entropy_init(&entropy); mbedtls_ctr_drbg_init(&rng); }
    ~Tls() { if(socket_!=INVALID_SOCKET) closesocket(socket_); mbedtls_ssl_free(&ssl); mbedtls_ssl_config_free(&config); mbedtls_x509_crt_free(&ca); mbedtls_ctr_drbg_free(&rng); mbedtls_entropy_free(&entropy); }
    void connect(const std::string& host) {
        addrinfo hints{},*addresses=nullptr; hints.ai_family=AF_UNSPEC; hints.ai_socktype=SOCK_STREAM;
        require(getaddrinfo(host.c_str(),"443",&hints,&addresses)==0,"Could not resolve the download service. Check your internet connection.");
        std::unique_ptr<addrinfo,decltype(&freeaddrinfo)> resolved(addresses,freeaddrinfo);
        for(auto a=addresses;a;a=a->ai_next) {
            check_cancelled(); SOCKET candidate=::socket(a->ai_family,a->ai_socktype,a->ai_protocol); if(candidate==INVALID_SOCKET) continue;
            u_long nonblocking=1; ioctlsocket(candidate,FIONBIO,&nonblocking);
            int status=::connect(candidate,a->ai_addr,static_cast<int>(a->ai_addrlen));
            if(status==SOCKET_ERROR && WSAGetLastError()==WSAEWOULDBLOCK) {
                fd_set writable,errors; FD_ZERO(&writable); FD_ZERO(&errors); FD_SET(candidate,&writable); FD_SET(candidate,&errors); timeval timeout{20,0};
                int count=select(0,nullptr,&writable,&errors,&timeout); int error=0,bytes=sizeof error;
                status=count>0 && FD_ISSET(candidate,&writable) && getsockopt(candidate,SOL_SOCKET,SO_ERROR,reinterpret_cast<char*>(&error),&bytes)==0 && error==0?0:SOCKET_ERROR;
            }
            if(status==0) { nonblocking=0; ioctlsocket(candidate,FIONBIO,&nonblocking); socket_=candidate; break; } closesocket(candidate);
        }
        require(socket_!=INVALID_SOCKET,"Could not connect to the download service. Check your connection and firewall.");
        DWORD timeout=45000; setsockopt(socket_,SOL_SOCKET,SO_RCVTIMEO,reinterpret_cast<char*>(&timeout),sizeof timeout); setsockopt(socket_,SOL_SOCKET,SO_SNDTIMEO,reinterpret_cast<char*>(&timeout),sizeof timeout);
        const unsigned char personalization[]="Dungeon-Runners-Launcher";
        require(mbedtls_ctr_drbg_seed(&rng,mbedtls_entropy_func,&entropy,personalization,sizeof personalization)==0,"Secure random initialization failed.");
        auto roots=resource(101); roots.push_back(0);
        require(mbedtls_x509_crt_parse(&ca,roots.data(),roots.size())==0,"Bundled certificate store is invalid.");
        require(mbedtls_ssl_config_defaults(&config,MBEDTLS_SSL_IS_CLIENT,MBEDTLS_SSL_TRANSPORT_STREAM,MBEDTLS_SSL_PRESET_DEFAULT)==0,"TLS initialization failed.");
        mbedtls_ssl_conf_min_tls_version(&config,MBEDTLS_SSL_VERSION_TLS1_2);
        mbedtls_ssl_conf_authmode(&config,MBEDTLS_SSL_VERIFY_REQUIRED); mbedtls_ssl_conf_ca_chain(&config,&ca,nullptr); mbedtls_ssl_conf_rng(&config,mbedtls_ctr_drbg_random,&rng);
        require(mbedtls_ssl_setup(&ssl,&config)==0 && mbedtls_ssl_set_hostname(&ssl,host.c_str())==0,"TLS hostname validation could not start.");
        mbedtls_ssl_set_bio(&ssl,&socket_,send_bytes,receive_bytes,nullptr);
        int result; do { check_cancelled(); result=mbedtls_ssl_handshake(&ssl); } while(result==MBEDTLS_ERR_SSL_WANT_READ || result==MBEDTLS_ERR_SSL_WANT_WRITE);
        if(result!=0) {
            uint32_t flags=mbedtls_ssl_get_verify_result(&ssl);
            if(flags) throw std::runtime_error("HTTPS certificate verification failed. Check the computer date and time, then retry. The connection was not accepted.");
            char detail[180]; mbedtls_strerror(result,detail,sizeof detail); throw std::runtime_error(std::string("Secure connection failed: ")+detail);
        }
    }
    void send(const std::string& text) { size_t pos=0; while(pos<text.size()) { check_cancelled(); int n=mbedtls_ssl_write(&ssl,reinterpret_cast<const unsigned char*>(text.data()+pos),text.size()-pos); require(n>0,"Connection closed while sending the request."); pos+=n; } }
    size_t receive(unsigned char* data,size_t count) { check_cancelled(); int n=mbedtls_ssl_read(&ssl,data,count); if(n==MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY) return 0; require(n>=0,"The secure download was interrupted. Select Update to retry."); return static_cast<size_t>(n); }
};
class Reader {
    Tls& tls; unsigned char buffer[16384]; size_t pos=0,end=0;
public:
    explicit Reader(Tls& connection):tls(connection){}
    size_t get(unsigned char* output,size_t size) { if(pos==end) { pos=0; end=tls.receive(buffer,sizeof buffer); } size_t n=std::min(size,end-pos); memcpy(output,buffer+pos,n); pos+=n; return n; }
    std::string line(size_t limit) { std::string result; while(result.size()<limit) { unsigned char c; require(get(&c,1)==1,"Incomplete HTTP response."); if(c=='\n') { require(!result.empty()&&result.back()=='\r',"Invalid HTTP line."); result.pop_back(); return result; } result+=static_cast<char>(c); } throw std::runtime_error("HTTP response headers are too large."); }
};
static uint64_t number(const std::string& text,int radix) {
    require(!text.empty() && text.size()<24 && text.find_first_not_of(radix==16?"0123456789abcdefABCDEF":"0123456789")==std::string::npos,"Invalid response size.");
    uint64_t value=0; for(char c:text) { int digit=c<='9'?c-'0':tolower(c)-'a'+10; require(value<=(UINT64_MAX-digit)/radix,"Response size overflow."); value=value*radix+digit; } return value;
}
void download(const std::string& requested,const Path& target,uint64_t limit,const Progress& progress) {
    require(limit>0 && limit<=2147483648ULL,"Invalid download limit.");
    std::string url=requested; folders(directory(target)); no_links(target);
    for(int hop=0;hop<6;hop++) {
        require(allowed_url(url),"Untrusted download destination."); Url u=split_url(url); Tls tls; tls.connect(u.host);
        tls.send("GET "+u.path+" HTTP/1.1\r\nHost: "+u.host+"\r\nUser-Agent: Dungeon-Runners-Launcher/1.0.3\r\nAccept: */*\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n");
        Reader reader(tls); auto status=reader.line(1024); require(status.size()>=12 && status.compare(0,9,"HTTP/1.1 ")==0,"Invalid HTTP response."); int code=static_cast<int>(number(status.substr(9,3),10));
        std::map<std::string,std::string> headers; size_t header_size=status.size();
        while(true) {
            auto line=reader.line(16384); header_size+=line.size(); require(header_size<=65536,"Response headers are too large."); if(line.empty()) break;
            auto separator=line.find(':'); require(separator!=std::string::npos && separator>0,"Invalid HTTP header.");
            auto key=line.substr(0,separator),value=line.substr(separator+1); std::transform(key.begin(),key.end(),key.begin(),[](unsigned char c){return static_cast<char>(tolower(c));});
            while(!value.empty()&&(value.front()==' '||value.front()=='\t')) value.erase(value.begin());
            while(!value.empty()&&(value.back()==' '||value.back()=='\t')) value.pop_back();
            if(key=="content-length"||key=="transfer-encoding"||key=="location"||key=="content-encoding") require(headers.emplace(key,value).second,"Duplicate HTTP framing header.");
        }
        if(code==301||code==302||code==303||code==307||code==308) { require(headers.count("location")!=0,"Download redirect is missing."); url=headers.at("location"); if(!url.empty()&&url[0]=='/' && (url.size()==1||url[1]!='/')) url="https://"+u.host+url; continue; }
        require(code==200,code==403||code==429?"The download service is temporarily limiting requests. Please retry later.":"Download service returned HTTP "+std::to_string(code)+".");
        require(!headers.count("content-encoding")||headers.at("content-encoding")=="identity","Unexpected compressed HTTP response.");
        bool chunked=headers.count("transfer-encoding")!=0; if(chunked) require(headers.at("transfer-encoding")=="chunked"&&!headers.count("content-length"),"Invalid HTTP framing.");
        uint64_t total=headers.count("content-length")?number(headers.at("content-length"),10):0; require(total<=limit,"Download exceeds its allowed size.");
        HANDLE file=CreateFileW(target.c_str(),GENERIC_WRITE,0,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr); require(file!=INVALID_HANDLE_VALUE,"Cannot create download file.");
        try {
            uint64_t done=0; unsigned char buffer[65536];
            auto copy=[&](uint64_t count,bool exact) {
                while(count) { size_t n=reader.get(buffer,static_cast<size_t>(std::min<uint64_t>(count,sizeof buffer))); if(!n) { require(!exact,"Download ended early."); break; } require(n<=limit-done,"Download exceeds its allowed size."); DWORD written; require(WriteFile(file,buffer,static_cast<DWORD>(n),&written,nullptr)&&written==n,"Cannot save download."); done+=n;count-=n; if(progress)progress("Downloading",done,total); }
            };
            if(chunked) {
                while(true) { auto line=reader.line(1024); auto semi=line.find(';'); uint64_t size=number(line.substr(0,semi),16); require(size<=limit-done,"Download exceeds its allowed size."); if(!size) { size_t tail=0; while(!(line=reader.line(8192)).empty()) { tail+=line.size(); require(tail<32768,"HTTP trailers are too large."); } break; } copy(size,true); require(reader.line(2).empty(),"Invalid chunk terminator."); }
            } else if(headers.count("content-length")) copy(total,true); else copy(limit+1,false);
            require(FlushFileBuffers(file),"Cannot flush download."); CloseHandle(file); return;
        } catch(...) { CloseHandle(file); DeleteFileW(target.c_str()); throw; }
    }
    throw std::runtime_error("Too many download redirects.");
}
Bytes download(const std::string& url,uint64_t limit) {
    Path cache=under(local_data(),"Dungeon Runners Launcher/network"); folders(cache); wchar_t name[MAX_PATH]; require(GetTempFileNameW(cache.c_str(),L"get",0,name)!=0,"Cannot create download buffer.");
    try { download(url,name,limit); auto data=read(name,limit); DeleteFileW(name); return data; } catch(...) { DeleteFileW(name); throw; }
}
}
