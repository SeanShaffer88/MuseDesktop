; MuseDesktop installer (NSIS) — per-user install, no admin required.
;
; Build (from the repo root, with the verified release publish staged):
;   makensis /DVERSION=0.3.0 /DSTAGE=<dir containing the publish output> installer\MuseDesktop.nsi
; Produces MuseDesktop-Setup-v<VERSION>.exe next to this script.
;
; Design notes (see DECISIONS.md, 2026-10-23):
; - Per-user install to %LOCALAPPDATA%\Programs\MuseDesktop, HKCU uninstall
;   registration: matches the app's per-user data model and needs no UAC.
; - Uninstall removes the program and shortcuts but KEEPS the user's data
;   (%APPDATA%\MuseDesktop settings/log and every profile folder under
;   %LOCALAPPDATA%\MuseDesktop) unless the user answers Yes to the
;   "also delete" prompt — the same folders a zip user would delete by hand.

!ifndef VERSION
  !define VERSION "0.3.0"
!endif
!ifndef STAGE
  !error "Pass /DSTAGE=<publish output dir>"
!endif

Unicode true
Name "MuseDesktop"
OutFile "MuseDesktop-Setup-v${VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\MuseDesktop"
InstallDirRegKey HKCU "Software\MuseDesktop" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "MuseDesktop"
VIAddVersionKey "FileDescription" "MuseDesktop Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "MIT License"

!include "MUI2.nsh"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\MuseDesktop.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Run MuseDesktop"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "MuseDesktop" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"
  File /r "${STAGE}\*.*"

  WriteRegStr HKCU "Software\MuseDesktop" "InstallDir" "$INSTDIR"

  ; Start Menu shortcut
  CreateDirectory "$SMPROGRAMS\MuseDesktop"
  CreateShortcut "$SMPROGRAMS\MuseDesktop\MuseDesktop.lnk" "$INSTDIR\MuseDesktop.exe"

  ; Uninstaller + Apps & Features registration (per-user)
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "DisplayName" "MuseDesktop"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "Publisher" "Sean Shaffer"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "DisplayIcon" "$INSTDIR\MuseDesktop.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "NoRepair" 1
SectionEnd

Section /o "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\MuseDesktop.lnk" "$INSTDIR\MuseDesktop.exe"
SectionEnd

LangString DESC_SecMain ${LANG_ENGLISH} "MuseDesktop program files (required)."
LangString DESC_SecDesktop ${LANG_ENGLISH} "Adds a MuseDesktop shortcut to your desktop."
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} $(DESC_SecMain)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} $(DESC_SecDesktop)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  ; Program files and shortcuts
  Delete "$SMPROGRAMS\MuseDesktop\MuseDesktop.lnk"
  RMDir "$SMPROGRAMS\MuseDesktop"
  Delete "$DESKTOP\MuseDesktop.lnk"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop"
  DeleteRegKey HKCU "Software\MuseDesktop"

  ; User data is kept by default (settings, log, and every sign-in profile).
  MessageBox MB_YESNO|MB_ICONQUESTION \
    "Also delete MuseDesktop's settings and sign-in data?$\n$\nThis removes %APPDATA%\MuseDesktop and the profile folders under %LOCALAPPDATA%\MuseDesktop, signing you out of Muse on this PC. Your chats are in your Muse account and are not affected." \
    IDNO keepData
    RMDir /r "$APPDATA\MuseDesktop"
    ; Profile folders: WebView2, WebView2-<id>, ... under %LOCALAPPDATA%\MuseDesktop
    RMDir /r "$LOCALAPPDATA\MuseDesktop"
  keepData:
SectionEnd
