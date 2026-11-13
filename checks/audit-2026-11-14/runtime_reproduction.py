"""Compatibility entry point for the converted process regression suite."""
import pathlib
import runpy
runpy.run_path(str(pathlib.Path(__file__).with_name("runtime_regressions.py")), run_name="__main__")
