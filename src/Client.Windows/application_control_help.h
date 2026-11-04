#pragma once

namespace dr {
static const char ApplicationControlTitle[]="Windows blocked the application";
static const char ApplicationControlHelp[]="Windows Security > App & browser control > Smart App Control settings. If On, set Off and retry Play.\nThis disables this protection for all apps.";
static const char VerificationRepository[]="https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher";
static const char VerificationCommunity[]="https://discord.gg/RFpRET5ry";
inline const char* application_control_message(unsigned long error,const char* fallback) { return error==4551 ? ApplicationControlHelp : fallback; }
}
