//! One-click entry point for the OTD + Wintab32 shim stack.
//!
//! Double-click this exe next to an existing OpenTabletDriver portable install:
//! it makes sure the system-wide Wintab32.dll matches the shim bundled inside
//! this exe (asking for elevation only when a write is actually needed), then
//! starts OpenTabletDriver.UX.Wpf.exe from the folder it lives in.
//!
//! Nothing is downloaded and no OTD installation is replaced - it drives the
//! OTD copy that is already on disk, which is why it stays a few hundred KB.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, ERROR_SUCCESS};
use windows_sys::Win32::System::Threading::{WaitForSingleObject, INFINITE};
use windows_sys::Win32::UI::Shell::{ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW};

const DLL_X64: &[u8] = include_bytes!("../../assets/Wintab32_x64.dll");
const DLL_X86: &[u8] = include_bytes!("../../assets/Wintab32_x86.dll");

const UX_EXE: &str = "OpenTabletDriver.UX.Wpf.exe";

fn main() {
    let install_only = std::env::args().any(|a| a == "--install-shim");

    // --target-dir <path> installs only the 64-bit DLL into that folder instead
    // of System32. It exists so the write/backup/locked-file paths can be
    // exercised without touching the machine; it needs no elevation.
    let target_dir: Option<PathBuf> = std::env::args()
        .collect::<Vec<String>>()
        .windows(2)
        .find(|w| w[0] == "--target-dir")
        .map(|w| PathBuf::from(&w[1]));

    let base = match std::env::current_exe().ok().and_then(|p| p.parent().map(Path::to_path_buf)) {
        Some(dir) => dir,
        None => {
            println!("找不到自身路径，无法继续。");
            pause();
            return;
        }
    };

    println!("OpenTabletDriver Wintab 启动器");
    println!("目录: {}", base.display());

    if let Some(dir) = target_dir {
        let target = dir.join("Wintab32.dll");
        let _ = fs::create_dir_all(&dir);
        println!("测试模式: 目标 {}", target.display());
        install_one(&target, DLL_X64, "test", &base);
        return;
    }

    let system32 = PathBuf::from(std::env::var("SystemRoot").unwrap_or_else(|_| "C:\\Windows".into()))
        .join("System32\\Wintab32.dll");
    let syswow64 = PathBuf::from(std::env::var("SystemRoot").unwrap_or_else(|_| "C:\\Windows".into()))
        .join("SysWOW64\\Wintab32.dll");

    let need64 = !same_bytes(&system32, DLL_X64);
    let need86 = !same_bytes(&syswow64, DLL_X86);

    if need64 || need86 {
        println!(
            "系统里的 Wintab32.dll 与随附版本不一致（{}），需要安装。",
            match (need64, need86) {
                (true, true) => "64 位与 32 位都要换",
                (true, false) => "64 位要换",
                (false, true) => "32 位要换",
                (false, false) => "无需更换",
            }
        );
        if !is_admin() {
            println!("需要管理员权限，正在请求提升……（在弹出的 UAC 窗口点“是”）");
            if relaunch_elevated(&base) {
                // The elevated copy performs the install and exits; we carry on
                // and start OTD once it is done.
                if !same_bytes(&system32, DLL_X64) || !same_bytes(&syswow64, DLL_X86) {
                    println!("安装没有完成，Wintab32.dll 仍未更新。");
                    pause();
                    return;
                }
            } else {
                println!("提权被取消，未做任何修改。");
                pause();
                return;
            }
        } else {
            install(&system32, &syswow64, &base);
        }
    } else {
        println!("系统 Wintab32.dll 已是最新，跳过安装。");
    }

    if install_only {
        println!("只安装模式，结束。");
        pause();
        return;
    }

    launcher_start(&base);
    if std::env::args().any(|a| a == "--keep-open") {
        pause();
    }
}

fn pause() {
    println!("按任意键关闭窗口……");
    let mut line = String::new();
    let _ = std::io::stdin().read_line(&mut line);
}

fn same_bytes(path: &Path, want: &[u8]) -> bool {
    match fs::read(path) {
        Ok(bytes) => bytes == want,
        Err(_) => false,
    }
}

fn is_admin() -> bool {
    // A write test is the honest check: the token can be nominally
    // administrator while the process still runs at medium integrity.
    let probe = PathBuf::from(std::env::var("SystemRoot").unwrap_or_else(|_| "C:\\Windows".into()))
        .join("System32\\__wintab_write_probe.tmp");
    match fs::write(&probe, b"probe") {
        Ok(_) => {
            let _ = fs::remove_file(&probe);
            true
        }
        Err(_) => false,
    }
}

fn relaunch_elevated(base: &Path) -> bool {
    let exe = match std::env::current_exe() {
        Ok(p) => p,
        Err(_) => return false,
    };
    let mut info: SHELLEXECUTEINFOW = unsafe { std::mem::zeroed() };
    info.cbSize = std::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
    info.fMask = SEE_MASK_NOCLOSEPROCESS;
    let verb: Vec<u16> = "runas\0".encode_utf16().collect();
    let file: Vec<u16> = to_wide(&exe.to_string_lossy());
    let params: Vec<u16> = "--install-shim\0".encode_utf16().collect();
    let dir: Vec<u16> = to_wide(&base.to_string_lossy());
    info.lpVerb = verb.as_ptr();
    info.lpFile = file.as_ptr();
    info.lpParameters = params.as_ptr();
    info.lpDirectory = dir.as_ptr();
    info.nShow = 1;
    let ok = unsafe { ShellExecuteExW(&mut info) };
    if ok == 0 {
        let code = unsafe { GetLastError() };
        if code != ERROR_SUCCESS {
            return false;
        }
        return false;
    }
    if !info.hProcess.is_null() {
        unsafe {
            WaitForSingleObject(info.hProcess, INFINITE);
            CloseHandle(info.hProcess);
        }
    }
    true
}

fn install(system32: &Path, syswow64: &Path, base: &Path) {
    install_one(system32, DLL_X64, "System32", base);
    install_one(syswow64, DLL_X86, "SysWOW64", base);
}

fn install_one(path: &Path, bytes: &[u8], label: &str, base: &Path) {
    if same_bytes(path, bytes) {
        println!("{label}: 已是最新");
        return;
    }
    let backup_dir = base.join("WintabBackup");
    let _ = fs::create_dir_all(&backup_dir);
    if path.exists() {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);
        let name = format!("Wintab32-before-{stamp}-{label}.dll");
        let _ = fs::copy(path, backup_dir.join(&name));
    }
    match write_replace(path, bytes) {
        Ok(()) => println!("{label}: 已安装 {}", path.display()),
        Err(err) => println!("{label}: 安装失败 {err}"),
    }
}

fn write_replace(path: &Path, bytes: &[u8]) -> std::io::Result<()> {
    if fs::write(path, bytes).is_ok() {
        return Ok(());
    }
    // The file is mapped into a running application, so Windows refuses the
    // overwrite. Move it aside first; the next start of that application finds
    // the new copy and the old one stays around as a rollback.
    let stamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let aside = path.with_file_name(format!("Wintab32.old-{stamp}.dll"));
    if path.exists() {
        let _ = fs::rename(path, &aside);
    }
    fs::write(path, bytes)
}

fn launcher_start(base: &Path) {
    let mut candidates = vec![
        (base.join(UX_EXE), base.to_path_buf()),
        (base.join("portable-build").join(UX_EXE), base.join("portable-build")),
    ];
    if let Ok(extra) = std::env::var("OTD_BASE") {
        let dir = PathBuf::from(extra);
        candidates.push((dir.join(UX_EXE), dir));
    }

    for (exe, cwd) in candidates.iter().filter(|(p, _)| p.exists()) {
        println!("启动: {} (工作目录 {})", exe.display(), cwd.display());
        // A copy that dies on startup must not swallow the launch: some builds
        // on this machine abort inside WPF window hooking, and the neighbouring
        // portable build runs fine. Wait a few seconds and fall through.
        match Command::new(exe).current_dir(cwd).spawn() {
            Ok(mut child) => {
                let mut died = false;
                for _ in 0..40 {
                    std::thread::sleep(std::time::Duration::from_millis(100));
                    match child.try_wait() {
                        Ok(Some(status)) => {
                            println!("这个副本启动后立即退出（{}）。", status);
                            died = true;
                            break;
                        }
                        Ok(None) => {}
                        Err(_) => break,
                    }
                }
                if !died {
                    println!("已启动。这个窗口可以关掉。");
                    return;
                }
            }
            Err(err) => println!("启动失败: {err}"),
        }
    }

    println!("没有可用的 {UX_EXE}。请把本程序放在原有的 OpenTabletDriver 目录里再运行。");
    pause();
}

fn to_wide(s: &str) -> Vec<u16> {
    let mut out: Vec<u16> = s.encode_utf16().collect();
    out.push(0);
    out
}
