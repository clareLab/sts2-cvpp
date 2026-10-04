#![cfg(target_os = "linux")]

use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

static NEXT: AtomicU64 = AtomicU64::new(0);

fn start(executable: &str, arguments: &[&str]) -> (Child, PathBuf) {
    let directory = std::env::temp_dir().join(format!(
        "cvpp-worker-test-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ));
    std::fs::create_dir(&directory).unwrap();
    std::fs::write(directory.join(".cvpp-worker"), "").unwrap();
    let process = Command::new(env!("CARGO_BIN_EXE_cvpp-worker"))
        .arg(&directory)
        .arg(executable)
        .args(arguments)
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::inherit())
        .spawn()
        .unwrap();
    (process, directory)
}

fn until(mut predicate: impl FnMut() -> bool) {
    let started = Instant::now();
    while !predicate() {
        assert!(started.elapsed() < Duration::from_secs(5));
        std::thread::sleep(Duration::from_millis(10));
    }
}

#[test]
fn closed_parent_pipe_reaps_worker_and_sandbox() {
    let (mut process, directory) = start("sleep", &["60"]);
    drop(process.stdin.take());
    until(|| process.try_wait().unwrap().is_some());
    assert!(process.wait().unwrap().success());
    assert!(!directory.exists());
}

#[test]
fn stop_request_reaps_worker_and_preserves_logs() {
    use std::io::Write;

    let (mut process, directory) = start("sleep", &["60"]);
    process.stdin.as_mut().unwrap().write_all(&[0]).unwrap();
    until(|| process.try_wait().unwrap().is_some());
    assert!(process.wait().unwrap().success());
    assert!(directory.is_dir());
    std::fs::remove_dir_all(directory).unwrap();
}

#[test]
fn worker_exit_preserves_logs_for_parent() {
    let (mut process, directory) = start("true", &[]);
    until(|| process.try_wait().unwrap().is_some());
    assert!(process.wait().unwrap().success());
    assert!(directory.is_dir());
    std::fs::remove_dir_all(directory).unwrap();
}

#[test]
fn supervisor_death_kills_worker() {
    let (mut process, directory) = start("sleep", &["60"]);
    let children = format!("/proc/{0}/task/{0}/children", process.id());
    let mut id = String::new();
    until(|| {
        id = std::fs::read_to_string(&children).unwrap();
        !id.trim().is_empty()
    });
    let pid = rustix::process::Pid::from_raw(id.trim().parse().unwrap()).unwrap();
    let descriptor =
        rustix::process::pidfd_open(pid, rustix::process::PidfdFlags::empty()).unwrap();
    process.kill().unwrap();
    process.wait().unwrap();
    let mut events = [rustix::event::PollFd::new(
        &descriptor,
        rustix::event::PollFlags::IN,
    )];
    assert_eq!(
        rustix::event::poll(
            &mut events,
            Some(&rustix::event::Timespec {
                tv_sec: 5,
                tv_nsec: 0
            })
        )
        .unwrap(),
        1
    );
    std::fs::remove_dir_all(directory).unwrap();
}
