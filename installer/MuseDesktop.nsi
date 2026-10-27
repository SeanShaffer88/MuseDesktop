; MuseDesktop installer (NSIS) — per-user install, no admin required.
;
; Build (from the repo root, with the verified release publish staged):
;   makensis /DVERSION=0.4.0 /DARCH=x64 /DSTAGE=<dir containing the publish output> installer\MuseDesktop.nsi
; Produces MuseDesktop-Setup-v<VERSION>-<ARCH>.exe next to this script.
; ARCH is x64 or arm64 and must match the staged publish (v0.4.0+).
;
; Design notes (see DECISIONS.md, 2026-10-23):
; - Per-user install to %LOCALAPPDATA%\Programs\MuseDesktop, HKCU uninstall
;   registration: matches the app's per-user data model and needs no UAC.
; - Uninstall removes the program and shortcuts but KEEPS the user's data
;   (%APPDATA%\MuseDesktop settings/log and every profile folder under
;   %LOCALAPPDATA%\MuseDesktop) unless the user answers Yes to the
;   "also delete" prompt — the same folders a zip user would delete by hand.
; - musedesktop:// links (v1.0.0, S11 #99): the install registers the
;   per-user URL protocol under HKCU Software\Classes\musedesktop —
;   no admin, and uninstall removes it ONLY when its command still
;   points at this install (a user who moved the app or registered
;   another copy keeps what the key says). The app also keeps the
;   registration in line with its Settings toggle at runtime, which
;   is how portable (zip) installs register: the toggle, not this
;   script, owns that case.
;
; Seamless update handshake (v1.8.4, R7D):
; - An update must be hands-free after the user accepts it: when a
;   MuseDesktop instance is running, the installer closes it, installs
;   over it, and reopens it — no prompts anywhere in that logic, in the
;   wizard and in silent (/S) runs alike.
; - Detection uses the app's own single-instance mutex
;   (Local\MuseDesktop.SingleInstance, created by App.xaml.cs):
;   if OpenMutexW can open it, the app is running in this session.
; - The close is a handshake first: the installer runs the INSTALLED
;   MuseDesktop.exe --exit-for-update, which forwards an exit intent
;   to the running instance (Core: LaunchIntentKind.ExitForUpdate —
;   it applies immediately, even behind the app lock, and works with
;   the window hidden in the tray) and exits. Builds older than
;   v1.8.4 don't know the flag: they tolerate it (unknown arguments
;   are never fatal) and merely wake their window, so the handshake
;   politely does nothing there and the fallbacks below do the close.
; - Fallbacks, in order, each followed by a wait: ask every
;   MuseDesktop process to close its main window (PowerShell
;   CloseMainWindow — note a close-to-tray build hides instead of
;   exiting, which is why this is only a fallback), then force-kill
;   the process (taskkill /F). After the last wait the install
;   proceeds regardless: a file still locked surfaces as the ordinary
;   file error it always was.
; - Relaunch: a hidden final section reopens the app when (and only
;   when) it was running at the start. It is a section, not a finish-
;   page hook, because the finish page never runs in silent mode.
;   When the app was NOT running, today's behavior is unchanged (the
;   finish page's Run checkbox offers the launch).
;
; Installer audit (v0.13.0, S9 #86):
; - Install folder: the directory page (MUI_PAGE_DIRECTORY) offers a
;   choice, still per-user with no admin, and InstallDirRegKey reads
;   the recorded folder back on upgrade, so an upgrade installs into
;   the SAME folder the user picked before — both predate this audit.
; - Desktop shortcut: an optional Components-page section, OFF by
;   default (unchanged). The audit's one fix: the choice is recorded
;   in HKCU "Software\MuseDesktop" DesktopShortcut and pre-selected
;   on the next install/upgrade (.onInit), so upgrading no longer
;   silently drops a desktop shortcut the user once asked for.
;   Uninstall removes the registry key with the rest.

!ifndef VERSION
  !define VERSION "0.4.0"
!endif
!ifndef ARCH
  !define ARCH "x64"
!endif
!ifndef STAGE
  !error "Pass /DSTAGE=<publish output dir>"
!endif

Unicode true
Name "MuseDesktop"
OutFile "MuseDesktop-Setup-v${VERSION}-${ARCH}.exe"
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
!include "LogicLib.nsh"

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

; ── Seamless update state (R7D, see the design note at the top) ──
Var MdWasRunning  ; 1 when a MuseDesktop instance was running at install start
Var MdRunning     ; scratch: result of the last mutex check (1 = still running)
Var MdTries       ; scratch: poll-loop counter

; Is the app's single-instance mutex there? Sets $MdRunning.
; SYNCHRONIZE (0x00100000) is the least access that can open it; the
; handle is closed again at once — this only ever LOOKS.
!macro MD_CHECK_MUTEX
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, t "Local\MuseDesktop.SingleInstance") p .r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p $0)'
    StrCpy $MdRunning 1
  ${Else}
    StrCpy $MdRunning 0
  ${EndIf}
!macroend

; Poll every 500 ms until the mutex disappears, at most TRIES times
; (40 ≈ 20 s, 20 ≈ 10 s, 10 ≈ 5 s). Leaves $MdRunning current.
!macro MD_WAIT_UNTIL_GONE TRIES
  StrCpy $MdTries ${TRIES}
  ${Do}
    !insertmacro MD_CHECK_MUTEX
    ${If} $MdRunning == 0
      ${ExitDo}
    ${EndIf}
    IntOp $MdTries $MdTries - 1
    ${If} $MdTries <= 0
      ${ExitDo}
    ${EndIf}
    Sleep 500
  ${Loop}
!macroend

Section "MuseDesktop" SecMain
  SectionIn RO

  ; ── R7D: close the running app BEFORE any file is copied ──
  ; (design note at the top). No prompts: this is invisible in the
  ; wizard and in silent runs alike.
  !insertmacro MD_CHECK_MUTEX
  StrCpy $MdWasRunning $MdRunning
  ${If} $MdWasRunning == 1
    ; The handshake: the installed build forwards the exit intent
    ; and its second process exits at once; ExecWait returns when it
    ; has. (A missing exe — the mutex outlived its install — just
    ; sets the error flag, and the waits/fallbacks below still run.)
    ExecWait '"$INSTDIR\MuseDesktop.exe" --exit-for-update'
    !insertmacro MD_WAIT_UNTIL_GONE 40
    ${If} $MdRunning == 1
      ; Fallback 1: ask every MuseDesktop process to close its
      ; main window. ($$ is NSIS's literal-$ escape: PowerShell's $_
      ; must reach PowerShell, not NSIS's variable expansion.)
      nsExec::ExecToStack 'powershell -NoProfile -WindowStyle Hidden -Command "Get-Process MuseDesktop -ErrorAction SilentlyContinue | ForEach-Object { $$_.CloseMainWindow() | Out-Null }"'
      Pop $0
      Pop $0
      !insertmacro MD_WAIT_UNTIL_GONE 20
    ${EndIf}
    ${If} $MdRunning == 1
      ; Fallback 2: force it. The user's data is never in the
      ; program folder, and the app autosaves as it goes; a forced
      ; close here loses nothing the graceful paths would have kept.
      nsExec::ExecToStack 'taskkill /F /IM MuseDesktop.exe'
      Pop $0
      Pop $0
      !insertmacro MD_WAIT_UNTIL_GONE 10
    ${EndIf}
  ${EndIf}

  SetOutPath "$INSTDIR"
  File /r "${STAGE}\*.*"

  WriteRegStr HKCU "Software\MuseDesktop" "InstallDir" "$INSTDIR"

  ; Start Menu shortcut
  CreateDirectory "$SMPROGRAMS\MuseDesktop"
  CreateShortcut "$SMPROGRAMS\MuseDesktop\MuseDesktop.lnk" "$INSTDIR\MuseDesktop.exe"

  ; musedesktop:// protocol registration (S11 #99) — per-user, the
  ; standard URL-protocol key shape. The in-app toggle syncs the same
  ; key at runtime; the two writers always write the same values.
  WriteRegStr HKCU "Software\Classes\musedesktop" "" "URL:MuseDesktop Link"
  WriteRegStr HKCU "Software\Classes\musedesktop" "URL Protocol" ""
  WriteRegStr HKCU "Software\Classes\musedesktop\shell\open\command" "" '"$INSTDIR\MuseDesktop.exe" "%1"'

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

; S9 #86: record the desktop-shortcut choice for the next
; install/upgrade (read back by .onInit). Always runs.
Section "-RecordChoices" SecRecord
  SectionIn RO
  SectionGetFlags ${SecDesktop} $0
  IntOp $0 $0 & 1
  WriteRegDWORD HKCU "Software\MuseDesktop" "DesktopShortcut" $0
SectionEnd

; R7D: reopen the app after a seamless update — only when it was
; running when the installer started (see the handshake at the top of
; SecMain). A hidden LAST section so it runs after files, shortcuts,
; and the recorded choices, and in silent (/S) mode too — the finish
; page's Run checkbox never fires there. When the app was not
; running, nothing happens here and the finish page offers the
; launch exactly as before.
Section "-RelaunchAfterUpdate" SecRelaunch
  SectionIn RO
  ${If} $MdWasRunning == 1
    Exec '"$INSTDIR\MuseDesktop.exe"'
  ${EndIf}
SectionEnd

; S9 #86: restore the recorded desktop-shortcut choice (see the audit
; note at the top) so the Components page reflects what this user
; chose last time. First install: no value recorded, the section
; keeps its default (off). Defined after the sections on purpose —
; section constants only exist once their Section command is read.
Function .onInit
  ReadRegDWORD $0 HKCU "Software\MuseDesktop" "DesktopShortcut"
  ${If} $0 == 1
    SectionSetFlags ${SecDesktop} 1
  ${EndIf}
FunctionEnd

LangString DESC_SecMain ${LANG_ENGLISH} "MuseDesktop program files (required)."
LangString DESC_SecDesktop ${LANG_ENGLISH} "Adds a MuseDesktop shortcut to your desktop."
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} $(DESC_SecMain)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} $(DESC_SecDesktop)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  ; musedesktop:// registration (S11 #99): remove it only when it is
  ; still THIS install's command — never another copy's.
  ReadRegStr $0 HKCU "Software\Classes\musedesktop\shell\open\command" ""
  ${If} $0 == '"$INSTDIR\MuseDesktop.exe" "%1"'
    DeleteRegKey HKCU "Software\Classes\musedesktop"
  ${EndIf}

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
