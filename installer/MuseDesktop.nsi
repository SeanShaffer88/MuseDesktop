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
; Seamless update handshake (v1.8.4, R7D; rebuilt v1.8.5, R7D1):
; - An update must be hands-free after the user accepts it: when a
;   MuseDesktop instance is running, the installer closes it and
;   installs over it — no prompts anywhere in that logic, in the
;   wizard and in silent (/S) runs alike.
; - FIELD FAILURE (v1.8.4, 2026-10-27) — why detection changed:
;   v1.8.4 detected the running app by opening its single-instance
;   mutex (System plugin, OpenMutexW on
;   Local\MuseDesktop.SingleInstance). On SEAN's machine that check
;   NEVER fired: with MuseDesktop visibly running, the installer
;   behaved exactly as if nothing was running — the looked-up name
;   never matched a live instance, so no close was ever attempted,
;   MdWasRunning stayed 0, and the only relaunch was the finish
;   page's stock Run checkbox firing mid-close. Detection is
;   PROCESS-based now (MD_CHECK_RUNNING): PowerShell's Get-Process
;   answers the literal question "is MuseDesktop.exe running",
;   and the answer rides the exit code — no object names, no
;   handles, no plugin call shapes to get silently wrong. Every
;   step of the close chain also prints a DetailPrint line, so a
;   future field failure leaves its story in the wizard's details
;   view instead of silence.
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
; - Relaunch (SEAN, 2026-10-27): the finish page's "Run MuseDesktop"
;   checkbox is the ONLY interactive relaunch — MUI fires it as the
;   wizard closes, so the app starts after the installer is gone,
;   and only when the box is ticked. When the app WAS running at
;   install start, MdFinishPageShow pre-ticks the box (bring back
;   what was open); the user's untick always wins, and when nothing
;   was running the box is left exactly as MUI ships it. Silent (/S)
;   runs have no finish page, so the one exception: a hidden last
;   section relaunches the app when it was running AND the run is
;   silent — in-app installs have no checkbox, and the click on
;   Install inside the app is the consent.
;
; Saved-password wipe (v1.8.8, R7F — the owner's rule: passwords
; are never saved in this app):
; - Versions before v1.8.8 let the web view save passwords. Upgrading
;   removes them: after the close chain above (the app must not be
;   holding the files) and before the new files copy, the installer
;   deletes the stored-password database — the files named exactly
;   "Login Data" and "Login Data-journal" — from every profile's
;   WebView2 folder under %LOCALAPPDATA%\MuseDesktop (the default
;   profile's "WebView2" and every "WebView2-<id>" sibling), in the
;   two places Chromium keeps that database: the folder itself and
;   its "Default" subfolder.
; - ONLY those two names, only in those places. Cookies — what keeps
;   the user signed in — are never touched, and neither is anything
;   else. Every deletion and every not-found prints a DetailPrint
;   line; a file that can't be deleted warns in the details view
;   and the install continues regardless (the app's own startup
;   sweep, Core SavedPasswordSweep, retries the same wipe on every
;   launch, which also covers portable-zip copies that never run
;   this installer).
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
; R7D1: pre-tick the finish page's Run box when the app was running
; (see MdFinishPageShow). The define must sit immediately before the
; FINISH insert — MUI consumes (and undefines) it in that page's
; SHOW hook, after the checkbox exists and before it is displayed.
!define MUI_PAGE_CUSTOMFUNCTION_SHOW MdFinishPageShow
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ── Seamless update state (R7D/R7D1, see the design note) ──
Var MdWasRunning  ; 1 when a MuseDesktop instance was running at install start
Var MdRunning     ; scratch: result of the last process check (1 = still running)
Var MdCheckCode   ; scratch: exit-code text of the last process check
Var MdTries       ; scratch: poll-loop counter
Var MdPwDir       ; R7F scratch: profile folder the saved-password wipe is working on
Var MdPwFind      ; R7F scratch: FindFirst/FindNext handle for the profile-folder walk

; Is MuseDesktop.exe running? Sets $MdRunning. The answer rides
; PowerShell's EXIT CODE — 0 = a MuseDesktop process exists, 3 =
; none does — because an exit code cannot be misread the way the
; v1.8.4 mutex lookup was (see the design note). Any other outcome
; (PowerShell missing, an "error" return) reads as "not running".
; ExecToStack pushes the exit code on top of the output: the first
; Pop takes the code, the second discards the (empty) output.
!macro MD_CHECK_RUNNING
  nsExec::ExecToStack 'powershell -NoProfile -WindowStyle Hidden -Command "if (Get-Process MuseDesktop -ErrorAction SilentlyContinue) { exit 0 } else { exit 3 }"'
  Pop $MdCheckCode
  Pop $0
  ${If} $MdCheckCode == 0
    StrCpy $MdRunning 1
  ${Else}
    StrCpy $MdRunning 0
  ${EndIf}
!macroend

; Poll every 500 ms until no MuseDesktop process remains, at most
; TRIES times (30 ≈ 15 s, 20 ≈ 10 s, 10 ≈ 5 s — each cycle also
; pays a PowerShell start, so real waits run a little longer).
; Leaves $MdRunning current.
!macro MD_WAIT_UNTIL_GONE TRIES
  StrCpy $MdTries ${TRIES}
  ${Do}
    !insertmacro MD_CHECK_RUNNING
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

; R7F: delete ONE saved-password file (path relative to $MdPwDir),
; saying what happened either way. A failed Delete never aborts the
; install — it warns here and the app's startup sweep retries later.
!macro MD_DELETE_SAVED_PASSWORD REL
  ${If} ${FileExists} "$MdPwDir\${REL}"
    Delete "$MdPwDir\${REL}"
    ${If} ${FileExists} "$MdPwDir\${REL}"
      DetailPrint "Saved-password cleanup: couldn't remove ${REL} from $MdPwDir — continuing anyway (the app retries at its next launch)."
    ${Else}
      DetailPrint "Saved-password cleanup: removed ${REL} from $MdPwDir."
    ${EndIf}
  ${Else}
    DetailPrint "Saved-password cleanup: no ${REL} in $MdPwDir."
  ${EndIf}
!macroend

; R7F: wipe the saved-password database from the profile folder in
; $MdPwDir — the two file names, in the two places Chromium keeps
; them (the folder itself and its Default subfolder). Nothing else
; in the folder is ever named here, so cookies and sign-ins survive.
Function MdWipeProfilePasswords
  !insertmacro MD_DELETE_SAVED_PASSWORD "Login Data"
  !insertmacro MD_DELETE_SAVED_PASSWORD "Login Data-journal"
  !insertmacro MD_DELETE_SAVED_PASSWORD "Default\Login Data"
  !insertmacro MD_DELETE_SAVED_PASSWORD "Default\Login Data-journal"
FunctionEnd

Section "MuseDesktop" SecMain
  SectionIn RO

  ; ── R7D/R7D1: close the running app BEFORE any file is copied ──
  ; (design note at the top). No prompts: this is invisible in the
  ; wizard and in silent runs alike. The DetailPrint lines are the
  ; evidence trail (the v1.8.4 failure left none): open the install
  ; details view and the close tells its own story.
  !insertmacro MD_CHECK_RUNNING
  StrCpy $MdWasRunning $MdRunning
  ${If} $MdWasRunning == 1
    DetailPrint "Seamless update: MuseDesktop is running — asking it to close."
    ; The handshake: the installed build forwards the exit intent
    ; and its second process exits at once; ExecWait returns when it
    ; has. (A missing exe just sets the error flag, and the
    ; waits/fallbacks below still run.)
    ExecWait '"$INSTDIR\MuseDesktop.exe" --exit-for-update'
    !insertmacro MD_WAIT_UNTIL_GONE 30
    ${If} $MdRunning == 0
      DetailPrint "Seamless update: MuseDesktop closed."
    ${EndIf}
    ${If} $MdRunning == 1
      DetailPrint "Seamless update: asking Windows to close MuseDesktop."
      ; Fallback 1: ask every MuseDesktop process to close its
      ; main window. ($$ is NSIS's literal-$ escape: PowerShell's $_
      ; must reach PowerShell, not NSIS's variable expansion.)
      nsExec::ExecToStack 'powershell -NoProfile -WindowStyle Hidden -Command "Get-Process MuseDesktop -ErrorAction SilentlyContinue | ForEach-Object { $$_.CloseMainWindow() | Out-Null }"'
      Pop $0
      Pop $0
      !insertmacro MD_WAIT_UNTIL_GONE 20
      ${If} $MdRunning == 0
        DetailPrint "Seamless update: MuseDesktop closed."
      ${EndIf}
    ${EndIf}
    ${If} $MdRunning == 1
      DetailPrint "Seamless update: forcing MuseDesktop closed."
      ; Fallback 2: force it. The user's data is never in the
      ; program folder, and the app autosaves as it goes; a forced
      ; close here loses nothing the graceful paths would have kept.
      nsExec::ExecToStack 'taskkill /F /IM MuseDesktop.exe'
      Pop $0
      Pop $0
      !insertmacro MD_WAIT_UNTIL_GONE 10
      ${If} $MdRunning == 0
        DetailPrint "Seamless update: MuseDesktop closed."
      ${Else}
        DetailPrint "Seamless update: MuseDesktop is still running — continuing anyway."
      ${EndIf}
    ${EndIf}
  ${EndIf}

  ; ── R7F: wipe passwords saved by older versions ──
  ; After the close chain above (nothing is holding the files now),
  ; before the new program files copy. Every profile folder: the
  ; default profile's own folder, then every WebView2-<id> sibling.
  ; Only the two stored-password names are ever deleted — cookies
  ; (sign-ins) and everything else stay exactly as they were.
  DetailPrint "Saved-password cleanup: removing passwords saved by older MuseDesktop versions (sign-ins and cookies are kept)."
  StrCpy $MdPwDir "$LOCALAPPDATA\MuseDesktop\WebView2"
  Call MdWipeProfilePasswords
  FindFirst $MdPwFind $MdPwDir "$LOCALAPPDATA\MuseDesktop\WebView2-*"
  MdPwLoop:
    StrCmp $MdPwDir "" MdPwDone
    Push $MdPwDir
    StrCpy $MdPwDir "$LOCALAPPDATA\MuseDesktop\$MdPwDir"
    Call MdWipeProfilePasswords
    Pop $MdPwDir
    FindNext $MdPwFind $MdPwDir
    IfErrors MdPwDone
    Goto MdPwLoop
  MdPwDone:
  FindClose $MdPwFind

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

; R7D1: the silent-mode relaunch exception (see the design note).
; Interactively the finish page's Run checkbox is the ONLY relaunch
; and this section must not fire — a /S run has no finish page, so a
; hidden LAST section reopens the app when (and only when) it was
; running AND the run is silent. In-app installs are that silent
; path, and their consent is the Install click inside the app.
Section "-MdSilentRelaunch" SecSilentRelaunch
  SectionIn RO
  ${If} $MdWasRunning == 1
    ${If} ${Silent}
      Exec '"$INSTDIR\MuseDesktop.exe"'
    ${EndIf}
  ${EndIf}
SectionEnd

; R7D1: finish-page SHOW hook (wired by the define before
; MUI_PAGE_FINISH). MUI calls it after the finish page's controls
; exist and before the dialog shows: when the app was running at
; install start, pre-tick "Run MuseDesktop" — the very SendMessage
; MUI itself uses to check the box by default ($mui.FinishPage.Run
; is the checkbox's handle, MUI_FinishPage state in Finish.nsh;
; BM_SETCHECK/BST_CHECKED come from WinMessages.nsh via MUI2). The
; user's untick afterwards always wins; when nothing was running,
; the box keeps its stock state and this does nothing.
Function MdFinishPageShow
  ${If} $MdWasRunning == 1
    SendMessage $mui.FinishPage.Run ${BM_SETCHECK} ${BST_CHECKED} 0
  ${EndIf}
FunctionEnd

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
