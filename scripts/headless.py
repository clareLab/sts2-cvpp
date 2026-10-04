import argparse
import json
import os
import shutil
import signal
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("data", type=Path)
    parser.add_argument("--benchmark", action="store_true")
    parser.add_argument("--ui", action="store_true")
    args = parser.parse_args()
    source, data = args.source.resolve(), args.data.resolve()
    name = "ui" if args.ui else "benchmark" if args.benchmark else "headless"
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
        userdata = directory / "userdata"
        profile = userdata / "SlayTheSpire2"
        settings = profile / "default/1/settings.save"
        settings.parent.mkdir(parents=True)
        settings.write_text(
            json.dumps(
                {
                    "schema_version": 8,
                    "mod_settings": {"mods_enabled": True, "mod_list": []},
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
                status = process.wait(timeout=300 if args.benchmark or args.ui else 180)
            finally:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait(timeout=5)
        report_path = profile / "cvpp-selftest.json"
        if (profile / "cvpp-ui.png").is_file():
            shutil.copy2(profile / "cvpp-ui.png", results / "ui.png")
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
