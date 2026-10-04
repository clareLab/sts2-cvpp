import argparse
import json
import os
import shutil
import signal
import subprocess
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def owned_processes(sandbox):
    owned = []
    for process in Path("/proc").iterdir():
        if not process.name.isdigit():
            continue
        try:
            if str(sandbox).encode() in (process / "cmdline").read_bytes():
                owned.append(int(process.name))
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            pass
    return owned


def check_exit(process, profile, mode):
    marker = profile / "cvpp-exit.json"
    deadline = time.monotonic() + 130
    while not marker.is_file():
        if process.poll() is not None or time.monotonic() > deadline:
            raise RuntimeError("Exit probe did not become ready")
        time.sleep(0.05)
    probe = json.loads(marker.read_text())
    worker = Path(f"/proc/{probe['worker']}")
    started = time.monotonic()
    if mode not in {"normal", "menu"}:
        os.kill(probe["parent"], signal.SIGKILL)
    while True:
        try:
            alive = (worker / "stat").read_text().split(") ", 1)[1].split()[0] != "Z"
        except FileNotFoundError:
            alive = False
        if (
            not alive
            and not Path(probe["sandbox"]).exists()
            and not owned_processes(probe["sandbox"])
        ):
            break
        if time.monotonic() - started > 15:
            raise RuntimeError(f"Worker or sandbox survived {mode} parent exit: {probe}")
        time.sleep(0.05)
    status = process.wait(timeout=10)
    if mode in {"normal", "menu"} and status != 0:
        raise RuntimeError(f"Normal shutdown exited with {status}")
    return {"success": True, "mode": mode, "reclaimedSeconds": time.monotonic() - started}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("data", type=Path)
    parser.add_argument("--benchmark", action="store_true")
    parser.add_argument("--snapshot-probe", action="store_true")
    parser.add_argument("--ui", action="store_true")
    parser.add_argument("--mod", type=Path, action="append", default=[])
    parser.add_argument("--replay", type=Path)
    parser.add_argument("--save", type=Path)
    parser.add_argument("--product-only", action="store_true")
    parser.add_argument("--worker-benchmark", action="store_true")
    parser.add_argument("--snapshot-worker", action="store_true")
    parser.add_argument("--fixed-work", action="store_true")
    parser.add_argument("--exit", choices=["startup", "search", "paused", "normal", "menu"])
    args = parser.parse_args()
    if args.snapshot_probe and any(
        (
            args.ui,
            args.benchmark,
            args.worker_benchmark,
            args.product_only,
            args.exit,
            args.mod,
            args.save,
            args.replay,
        )
    ):
        parser.error("--snapshot-probe requires an isolated vanilla headless test")
    if args.worker_benchmark and (args.ui or args.benchmark or args.exit):
        parser.error("--worker-benchmark requires headless mode")
    if args.snapshot_worker and (not args.worker_benchmark or args.mod):
        parser.error("--snapshot-worker requires an isolated vanilla worker benchmark")
    if args.fixed_work and not args.worker_benchmark:
        parser.error("--fixed-work requires --worker-benchmark")
    source, data = args.source.resolve(), args.data.resolve()
    name = "ui" if args.ui else "benchmark" if args.benchmark else "headless"
    if args.worker_benchmark:
        name = "worker-benchmark"
        if args.snapshot_worker:
            name += "-snapshot"
    if args.snapshot_probe:
        name = "snapshot-probe"
    if args.exit:
        name = "exit-" + args.exit
    if args.mod:
        name += "-modded"
    package = ROOT / "artifacts/integration/dist/cvpp"
    results = ROOT / "artifacts/validation"
    instances = ROOT / "artifacts/workers"
    results.mkdir(parents=True, exist_ok=True)
    instances.mkdir(parents=True, exist_ok=True)
    (results / f"{name}.json").unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix="smoke-", dir=instances) as temporary:
        directory = Path(temporary)
        game = directory / "game"
        game.mkdir()
        for item in source.iterdir():
            if item.name in {"mods", "steam_appid.txt", data.name}:
                continue
            target = game / item.name
            if item.name == "SlayTheSpire2":
                shutil.copy2(item, target)
            else:
                target.symlink_to(item)
        (game / data.name).symlink_to(data, target_is_directory=True)
        shutil.copytree(package, game / "mods/cvpp")
        mod_list = []
        for index, source_mod in enumerate(args.mod):
            target_mod = game / "mods/environment" / str(index)
            shutil.copytree(source_mod, target_mod, symlinks=True)
            manifests = list(target_mod.glob("*.json"))
            for manifest in manifests:
                metadata = json.loads(manifest.read_text(encoding="utf-8-sig"))
                if "id" in metadata:
                    mod_list.append(
                        {"id": metadata["id"], "is_enabled": True, "source": "mods_directory"}
                    )
        userdata = directory / "userdata"
        profile = userdata / "SlayTheSpire2"
        settings = profile / "default/1/settings.save"
        settings.parent.mkdir(parents=True)
        settings.write_text(
            json.dumps(
                {
                    "schema_version": 8,
                    "mod_settings": {"mods_enabled": True, "mod_list": mod_list},
                    "volume_master": 0,
                    "skip_intro_logo": True,
                    "seen_ea_disclaimer": True,
                    "fullscreen": False,
                    "fps_limit": 0 if args.benchmark else 60,
                    "language": "eng",
                }
            )
        )
        (profile / ".cvpp-test-sandbox").touch()
        if args.replay:
            shutil.copy2(args.replay, profile / "cvpp-fixture.mcr")
        if args.save:
            shutil.copy2(args.save, profile / "cvpp-fixture.save")
        command = [
            str(game / "SlayTheSpire2"),
            "--headless",
            "--audio-driver",
            "Dummy",
            "--force-steam=off",
            "--cvpp-selftest",
        ]
        if args.benchmark:
            command.append("--cvpp-benchmark")
        if args.snapshot_probe:
            command.append("--cvpp-snapshot-probe")
        if args.worker_benchmark:
            command.append("--cvpp-worker-benchmark")
        if args.fixed_work:
            command.append("--cvpp-fixed-work")
        if args.product_only:
            command.append("--cvpp-product-only")
        if args.exit:
            command.extend(["--cvpp-product-only", "--cvpp-exit=" + args.exit])
        if args.ui:
            command.remove("--headless")
            command.extend(
                ["--cvpp-ui", "--rendering-method", "gl_compatibility", "--resolution", "1440x900"]
            )
        if shutil.which("steam-run"):
            command.insert(0, "steam-run")
        environment = os.environ | {
            "XDG_DATA_HOME": str(userdata),
            "XDG_CONFIG_HOME": str(directory / "config"),
            "LP_NUM_THREADS": "1",
            "DOTNET_PROCESSOR_COUNT": "2",
        }
        environment.pop("CVPP_SNAPSHOT_PROBE", None)
        if args.snapshot_worker:
            environment["CVPP_SNAPSHOT_PROBE"] = "1"
        with (results / f"{name}.log").open("w") as log:
            process = subprocess.Popen(
                command,
                cwd=game,
                env=environment,
                stdin=subprocess.DEVNULL,
                stdout=log,
                stderr=subprocess.STDOUT,
                start_new_session=True,
            )
            try:
                if args.exit:
                    exit_report = check_exit(process, profile, args.exit)
                    (results / f"{name}.json").write_text(json.dumps(exit_report, indent=2) + "\n")
                    print(f"PASS {args.exit} parent exit: worker and sandbox reclaimed")
                    return
                status = process.wait(timeout=300 if args.benchmark or args.ui else 180)
            finally:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait(timeout=5)
                for pid in owned_processes(directory):
                    try:
                        os.kill(pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
        report_path = profile / "cvpp-selftest.json"
        for screenshot in profile.glob("cvpp-ui-*.png"):
            shutil.copy2(screenshot, results / screenshot.name.removeprefix("cvpp-"))
        for log_path in profile.glob("cvpp-workers/last-*.log"):
            shutil.copy2(log_path, results / log_path.name)
        if not report_path.is_file():
            raise RuntimeError(f"Headless exited with {status}; see {results / f'{name}.log'}")
        report = json.loads(report_path.read_text())
        runtime_log = (results / f"{name}.log").read_text().split("[cvpp] SELFTEST", 1)[0]
        runtime_errors = [
            line
            for line in runtime_log.splitlines()
            if line.startswith(("[ERROR]", "ERROR:", "SCRIPT ERROR:"))
        ]
        if runtime_errors:
            report["success"] = False
            report["error"] = report["error"] or "\n".join(runtime_errors)
        (results / f"{name}.json").write_text(json.dumps(report, indent=2) + "\n")
        if status != 0 or not report["success"]:
            raise RuntimeError(report["error"] or f"Headless exited with {status}")
        print(f"PASS {len(report['passed'])} official headless checks")


if __name__ == "__main__":
    main()
