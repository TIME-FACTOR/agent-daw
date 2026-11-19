#!/bin/bash
# Builds and runs the OutputTap unit test with the system C++ compiler.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$(mktemp -d)"
${CXX:-c++} -std=c++20 -O2 -pthread -Wall -Wextra -o "$out/output_tap_test" "$here/OutputTapTest.cpp"
"$out/output_tap_test"
