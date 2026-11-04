#include "launcher.h"
#include "mbedtls/sha1.h"

namespace dr {
static uint32_t number(const Bytes& data,size_t offset) {
    require(offset+4<=data.size(),"Truncated addon texture.");
    return uint32_t(data[offset])|(uint32_t(data[offset+1])<<8)|(uint32_t(data[offset+2])<<16)|(uint32_t(data[offset+3])<<24);
}
Bytes addon_skin(const Path& root,const Bytes& metadata) {
    check_cancelled();require(!metadata.empty()&&metadata.size()<=131072,"Invalid addon UI metadata size.");
    Json manifest=parse(metadata);std::string index_hash=manifest.at("pkiSha1"),output_hash=manifest.at("sha256");int size=manifest.at("size");
    const auto& entries=manifest.at("entries");
    require(index_hash.size()==40&&index_hash.find_first_not_of("0123456789abcdefABCDEF")==std::string::npos&&valid_hash(output_hash)&&size>=36&&size<=4194304&&entries.is_array()&&entries.size()==9,"Invalid addon UI manifest.");
    const std::vector<std::pair<int,std::string>> identities={{2,"InGameUI4"},{2,"NewUI"},{2,"Font_Outline"},{17,"fonts\\Font_Outline_Metrics"},{9,"InGameMenu"},{9,"Options"},{3,"sylfaen"},{2,"mapicon_wishingwell"},{2,"Mystery_Wishing_Well_Icon"}};
    std::set<int> ids;std::map<std::pair<int,std::string>,Json> resources;
    for(const auto& entry:entries) {
        int id=entry.at("entry_id"),type=entry.at("type_code"),stored=entry.at("stored_size"),decoded=entry.at("decoded_size");
        int64_t offset=entry.at("package_offset");std::string name=entry.at("name");auto identity=std::make_pair(type,name);
        require(id>=0&&ids.insert(id).second&&std::find(identities.begin(),identities.end(),identity)!=identities.end()&&resources.emplace(identity,entry).second&&offset>=0&&stored>0&&stored<=4194304&&decoded>0&&decoded<=4194304&&valid_hash(entry.at("storedSha256"))&&valid_hash(entry.at("decodedSha256")),"Invalid addon UI resource identity.");
    }
    auto index=read(under(root,"game.pki"),33554432);unsigned char digest[20];
    require(!index.empty()&&mbedtls_sha1(index.data(),index.size(),digest)==0,"Cannot verify game UI index.");
    std::transform(index_hash.begin(),index_hash.end(),index_hash.begin(),[](unsigned char c){return static_cast<char>(tolower(c));});
    require(hex(digest,20)==index_hash,"Unsupported game UI index. Select Repair to restore the game data.");
    Path package_path=under(root,"game.pkg");no_links(package_path);uint64_t package_size=length(package_path);
    FILE* raw=nullptr;_wfopen_s(&raw,package_path.c_str(),L"rb");require(raw!=nullptr,"Cannot read game UI package.");
    std::unique_ptr<FILE,decltype(&fclose)> package(raw,fclose);std::vector<Bytes> data,components;
    for(const auto& identity:identities) {
        check_cancelled();const auto& entry=resources.at(identity);uint64_t offset=entry.at("package_offset");size_t stored_size=entry.at("stored_size"),decoded_size=entry.at("decoded_size");
        require(stored_size<=package_size&&offset<=package_size-stored_size,"Truncated game UI resource.");
        Bytes stored(stored_size);require(_fseeki64(package.get(),offset,SEEK_SET)==0&&fread(stored.data(),1,stored.size(),package.get())==stored.size()&&hash(stored)==entry.at("storedSha256"),"Game UI resource verification failed.");
        Bytes decoded;
        if(entry.at("flags").get<int>()&1) {
            decoded.resize(decoded_size);mz_stream stream{};require(mz_inflateInit(&stream)==MZ_OK,"Cannot decode game UI resource.");
            stream.next_in=stored.data();stream.avail_in=static_cast<unsigned>(stored.size());stream.next_out=decoded.data();stream.avail_out=static_cast<unsigned>(decoded.size());
            int result=mz_inflate(&stream,MZ_FINISH);bool valid=result==MZ_STREAM_END&&stream.total_out==decoded.size()&&stream.total_in==stored.size();mz_inflateEnd(&stream);
            require(valid,"Invalid compressed game UI resource.");
        } else decoded=std::move(stored);
        require(decoded.size()==decoded_size&&hash(decoded)==entry.at("decodedSha256"),"Decoded game UI verification failed.");data.push_back(std::move(decoded));
    }
    for(size_t i=0;i<3;i++) {
        const auto& raw_data=data[i];require(raw_data.size()>=128&&memcmp(raw_data.data(),"DDS ",4)==0&&memcmp(raw_data.data()+84,"DXT3",4)==0,"Unsupported addon UI texture.");
        auto width=number(raw_data,16),height=number(raw_data,12);require(width==height&&(width==512||width==1024)&&raw_data.size()-128>=width*height,"Invalid addon UI texture dimensions.");
        components.emplace_back(raw_data.begin()+128,raw_data.begin()+128+width*height);
    }
    require(data[3].size()==512,"Invalid addon font metrics.");Bytes metrics(256);
    for(size_t i=0;i<256;i++){unsigned value=data[3][i*2]|(unsigned(data[3][i*2+1])<<8);require(i<32||i>126||value<=30,"Unsupported addon font metrics.");metrics[i]=static_cast<unsigned char>(value+2);}
    components.push_back(std::move(metrics));
    require(data[6].size()>1024&&data[6][0]==0&&data[6][1]==1&&data[6][2]==0&&data[6][3]==0,"Unsupported addon value font.");
    for(size_t i:{size_t(6),size_t(7),size_t(8)})components.push_back(std::move(data[i]));
    Bytes output=bytes("DRUI0002");for(const auto& part:components)for(unsigned i=0;i<4;i++)output.push_back(static_cast<unsigned char>(part.size()>>(8*i)));
    for(const auto& part:components)output.insert(output.end(),part.begin(),part.end());
    require(output.size()==static_cast<size_t>(size)&&hash(output)==output_hash,"Addon UI bundle verification failed.");check_cancelled();return output;
}
}
