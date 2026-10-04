#[cfg(target_os = "linux")]
fn main() -> Result<(), Box<dyn std::error::Error>> {
    use std::path::PathBuf;

    let mut arguments = std::env::args_os().skip(1);
    let sandbox = PathBuf::from(arguments.next().ok_or("Missing worker sandbox")?);
    if !sandbox.join(".cvpp-worker").is_file() {
        return Err("Missing worker sandbox marker".into());
    }
    if run(arguments)? {
        std::fs::remove_dir_all(sandbox)?;
    }
    Ok(())
}

#[cfg(target_os = "linux")]
fn run(mut arguments: impl Iterator<Item = std::ffi::OsString>) -> std::io::Result<bool> {
    use rustix::event::{PollFd, PollFlags, poll};
    use rustix::process::{Pid, PidfdFlags, Signal, pidfd_open};
    use std::os::unix::process::CommandExt;
    use std::process::Command;

    rustix::process::setpriority_process(None, 10)?;
    let executable = arguments.next().ok_or(rustix::io::Errno::INVAL)?;
    let mut command = Command::new(executable);
    command.args(arguments);
    let supervisor = rustix::process::getpid();
    unsafe {
        command.pre_exec(move || {
            rustix::process::set_parent_process_death_signal(Some(Signal::KILL))?;
            if rustix::process::getppid() != Some(supervisor) {
                return Err(rustix::io::Errno::SRCH.into());
            }
            Ok(())
        });
    }
    let mut child = command.spawn()?;
    let result = (|| {
        let worker = pidfd_open(Pid::from_child(&child), PidfdFlags::empty())?;
        let input = std::io::stdin();
        let mut events = [
            PollFd::new(&input, PollFlags::IN),
            PollFd::new(&worker, PollFlags::IN),
        ];
        loop {
            match poll(&mut events, None) {
                Err(rustix::io::Errno::INTR) => {}
                result => {
                    return result
                        .map(|_| events[0].revents().contains(PollFlags::HUP))
                        .map_err(std::io::Error::from);
                }
            }
        }
    })();
    let killed = child.kill();
    let waited = child.wait();
    let orphaned = result?;
    if waited.is_err() {
        killed?;
    }
    waited?;
    Ok(orphaned)
}

#[cfg(not(target_os = "linux"))]
fn main() {
    eprintln!("The worker currently requires Linux.");
    std::process::exit(1);
}
