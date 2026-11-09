#pragma once
#include <windows.h>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <functional>
#include <stdexcept>
#include <string>
#include <vector>

namespace dr {
static const char MediaLibrary[]="ResampleDmo.dll";
static const wchar_t MediaCapability[]=L"/CapabilityName:Media.MediaFeaturePack~~~~0.0.1.0";
static const char MediaPackHelp[]="https://support.microsoft.com/en-us/windows/experience/platform-variants/media-feature-pack-for-windows-n";
static const char WindowsRepairHelp[]="https://support.microsoft.com/en-us/windows/experience/backup-recovery/use-the-system-file-checker-tool-to-repair-missing-or-corrupted-system-files";
static const char MediaRestartTitle[]="Restart Windows";
static const char MediaRestartText[]="Restart Windows to finish Media Feature Pack setup, then open the launcher and continue.";
void windows_media(const std::function<void(const std::string&,uint64_t,uint64_t)>& progress);

struct WindowsMediaError : std::runtime_error {
    const char* title;
    const char* help;
    WindowsMediaError(const char* title,const std::string& message,const char* help):std::runtime_error(message),title(title),help(help){}
};

inline bool media_pack_edition(DWORD edition) {
    switch(edition){
        case 0x1B: case 0x31: case 0x54: case 0x62: case 0x7A: case 0x7E:
        case 0x82: case 0x8D: case 0xA2: case 0xA5: case 0xB3: case 0xCA: return true;
        default:return false;
    }
}

inline WindowsMediaError media_pack_manual(const std::string& reason) {
    return {"Media Feature Pack required",reason+" Search for Optional features in Windows Settings. Add Media Feature Pack, then restart Windows.",MediaPackHelp};
}

inline WindowsMediaError windows_audio_file_missing() {
    return {"Windows audio file missing","Windows could not find ResampleDmo.dll. Open Microsoft help for repair steps.",WindowsRepairHelp};
}

inline bool direct_sound_needs_resampler(const std::vector<unsigned char>& image) {
    auto read_at=[&](size_t offset,auto& value){
        if(offset>image.size()||sizeof value>image.size()-offset)return false;
        std::memcpy(&value,image.data()+offset,sizeof value);return true;
    };
    IMAGE_DOS_HEADER dos{};
    if(!read_at(0,dos)||dos.e_magic!=IMAGE_DOS_SIGNATURE||dos.e_lfanew<static_cast<LONG>(sizeof dos))return false;
    IMAGE_NT_HEADERS32 pe{};const size_t header=static_cast<size_t>(dos.e_lfanew);
    if(!read_at(header,pe)||pe.Signature!=IMAGE_NT_SIGNATURE||pe.FileHeader.Machine!=IMAGE_FILE_MACHINE_I386
        ||pe.FileHeader.SizeOfOptionalHeader<sizeof(IMAGE_OPTIONAL_HEADER32)||pe.OptionalHeader.Magic!=IMAGE_NT_OPTIONAL_HDR32_MAGIC
        ||pe.OptionalHeader.NumberOfRvaAndSizes<=IMAGE_DIRECTORY_ENTRY_IMPORT||pe.FileHeader.NumberOfSections>96)return false;
    const size_t sections=header+sizeof(DWORD)+sizeof(IMAGE_FILE_HEADER)+pe.FileHeader.SizeOfOptionalHeader;
    auto region=[&](DWORD rva,size_t& available){
        available=0;
        for(WORD index=0;index<pe.FileHeader.NumberOfSections;index++){
            IMAGE_SECTION_HEADER section{};
            if(!read_at(sections+index*sizeof section,section))break;
            if(rva<section.VirtualAddress||rva-section.VirtualAddress>=section.SizeOfRawData)continue;
            const DWORD displacement=rva-section.VirtualAddress;
            const uint64_t offset=static_cast<uint64_t>(section.PointerToRawData)+displacement;
            if(offset>=image.size())break;
            available=std::min<size_t>(section.SizeOfRawData-displacement,image.size()-static_cast<size_t>(offset));
            return static_cast<size_t>(offset);
        }
        return size_t(0);
    };
    const auto table=pe.OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if(!table.VirtualAddress)return false;
    size_t available=0;const size_t first=region(table.VirtualAddress,available);
    const size_t length=std::min<size_t>(65536,std::min<size_t>(table.Size,available));
    for(size_t offset=0;offset+sizeof(IMAGE_IMPORT_DESCRIPTOR)<=length;offset+=sizeof(IMAGE_IMPORT_DESCRIPTOR)){
        IMAGE_IMPORT_DESCRIPTOR entry{};
        if(!read_at(first+offset,entry)||!entry.Name)break;
        size_t name_size=0;const size_t name=region(entry.Name,name_size);
        if(name_size>=sizeof MediaLibrary&&image[name+sizeof MediaLibrary-1]==0
            &&_strnicmp(reinterpret_cast<const char*>(image.data()+name),MediaLibrary,sizeof MediaLibrary-1)==0)return true;
    }
    return false;
}

}
