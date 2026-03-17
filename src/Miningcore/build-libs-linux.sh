#!/usr/bin/env bash
set -euo pipefail

OutDir=${1:?USO: build-libs-linux.sh <OutDir>}

mkdir -p "$OutDir"

export UNAME_S=$(uname -s)
export UNAME_P=$(uname -m || uname -p)

AES=$(../Native/check_cpu.sh aes && echo -maes || echo)
SSE2=$(../Native/check_cpu.sh sse2 && echo -msse2 || echo)
SSE3=$(../Native/check_cpu.sh sse3 && echo -msse3 || echo)
SSSE3=$(../Native/check_cpu.sh ssse3 && echo -mssse3 || echo)
PCLMUL=$(../Native/check_cpu.sh pclmul && echo -mpclmul || echo)
AVX=$(../Native/check_cpu.sh avx && echo -mavx || echo)
AVX2=$(../Native/check_cpu.sh avx2 && echo -mavx2 || echo)
AVX512F=$(../Native/check_cpu.sh avx512f && echo -mavx512f || echo)

export CPU_FLAGS="$AES $SSE2 $SSE3 $SSSE3 $PCLMUL $AVX $AVX2 $AVX512F"

HAVE_AES=$(../Native/check_cpu.sh aes && echo -D__AES__ || echo)
HAVE_SSE2=$(../Native/check_cpu.sh sse2 && echo -DHAVE_SSE2 || echo)
HAVE_SSE3=$(../Native/check_cpu.sh sse3 && echo -DHAVE_SSE3 || echo)
HAVE_SSSE3=$(../Native/check_cpu.sh ssse3 && echo -DHAVE_SSSE3 || echo)
HAVE_PCLMUL=$(../Native/check_cpu.sh pclmul && echo -DHAVE_PCLMUL || echo)
HAVE_AVX=$(../Native/check_cpu.sh avx && echo -DHAVE_AVX || echo)
HAVE_AVX2=$(../Native/check_cpu.sh avx2 && echo -DHAVE_AVX2 || echo)
HAVE_AVX512F=$(../Native/check_cpu.sh avx512f && echo -DHAVE_AVX512F || echo)

export HAVE_FEATURE="$HAVE_AES $HAVE_SSE2 $HAVE_SSE3 $HAVE_SSSE3 $HAVE_PCLMUL $HAVE_AVX $HAVE_AVX2 $HAVE_AVX512F"

# -------- Mostrar só warnings/errors (opcional) --------
if [[ "${FILTER_WARNERR:-}" == "1" ]]; then
  filter_re='^(In file included|[[:space:]]*[0-9]+ \| |.*(warning|error|note):)'
  run() { "$@" 2>&1 | grep -E "$filter_re" || true; }
else
  run() { "$@"; }
fi

# --------- Patchs GCC 13 ---------
maybe_patch_epee_cstdint() {
  local hdr
  for hdr in $(find .. -path '*/contrib/epee/include/serialization/keyvalue_serialization_overloads.h'); do
    [[ -f "$hdr" ]] || continue
    if ! grep -qE '^[[:space:]]*#include[[:space:]]*<cstdint>' "$hdr"; then
      echo "[patch] Adding <cstdint> to $hdr"
      sed -i '1i #include <cstdint>' "$hdr"
    fi
  done
}

maybe_patch_unistd() {
  local f
  for f in $(find ../Native -path '*/flex/cryptonote/*.c'); do
    [[ -f "$f" ]] || continue
    if grep -q "_exit" "$f" && ! grep -qE '^[[:space:]]*#include[[:space:]]*<unistd.h>' "$f"; then
      echo "[patch] Adding <unistd.h> to $f"
      sed -i '1i #include <unistd.h>' "$f"
    fi
  done
}

maybe_patch_epee_cstdint
maybe_patch_unistd

# ---------- helper ----------
build_and_move() {
  local dir="$1" so="$2"
  ( cd "$dir" && run make clean && run make )
  mv "$dir/$so" "$OutDir"
}

# ---------- libs nativas locais ----------
build_and_move ../Native/libmultihash        libmultihash.so
build_and_move ../Native/libbeamhash         libbeamhash.so
build_and_move ../Native/libetchash          libetchash.so
build_and_move ../Native/libethhash          libethhash.so
( cd ../Native/libethhashb3 && run make -j clean && run make -j ); mv ../Native/libethhashb3/libethhashb3.so "$OutDir"
build_and_move ../Native/libubqhash          libubqhash.so
build_and_move ../Native/libcryptonote       libcryptonote.so
build_and_move ../Native/libcryptonight      libcryptonight.so
build_and_move ../Native/libverushash        libverushash.so
build_and_move ../Native/libfiropow          libfiropow.so
build_and_move ../Native/libkawpow           libkawpow.so
build_and_move ../Native/libmeowpow          libmeowpow.so
build_and_move ../Native/libdero             libdero.so
build_and_move ../Native/libcortexcuckoocycle libcortexcuckoocycle.so
build_and_move ../Native/libprogpowz         libprogpowz.so
build_and_move ../Native/libzanonote         libzanonote.so
build_and_move ../Native/libmerakipow        libmerakipow.so || true
build_and_move ../Native/libphihash          libphihash.so   || true
build_and_move ../Native/libsccpow           libsccpow.so    || true

# ---------- secp256k1 -> libnexapow ----------
(
  cd /tmp
  rm -rf secp256k1
  git clone https://github.com/bitcoin-ABC/secp256k1
  cd secp256k1
  git checkout 04fabb44590c10a19e35f044d11eb5058aac65b2
  mkdir build && cd build
  run cmake -GNinja .. -DCMAKE_C_FLAGS=-fPIC \
      -DSECP256K1_ENABLE_MODULE_RECOVERY=OFF \
      -DSECP256K1_ENABLE_COVERAGE=OFF \
      -DSECP256K1_ENABLE_MODULE_SCHNORR=ON
  run ninja
)
( cd ../Native/libnexapow && cp /tmp/secp256k1/build/libsecp256k1.a . && run make clean && run make )
mv ../Native/libnexapow/libnexapow.so "$OutDir"

# ---------- RandomX -> librandomx ----------
(
  cd /tmp
  rm -rf RandomX
  git clone https://github.com/tevador/RandomX
  cd RandomX
  git checkout tags/v1.2.1
  mkdir build && cd build
  run cmake -DARCH=native -DCMAKE_C_FLAGS=-Wa,--noexecstack -DCMAKE_CXX_FLAGS=-Wa,--noexecstack ..
  run make
)
( cd ../Native/librandomx && cp /tmp/RandomX/build/librandomx.a . && run make clean && run make )
mv ../Native/librandomx/librandomx.so "$OutDir"

# ---------- RandomARQ (patch stdint.h) -> librandomarq ----------
(
  cd /tmp
  rm -rf RandomARQ
  git clone https://github.com/arqma/RandomARQ
  cd RandomARQ
  git checkout 3bcb6bafe63d70f8e6f78a0d431e71be2b638083

  UTILITY_HEADER="src/tests/utility.hpp"
  if [[ -f "$UTILITY_HEADER" ]]; then
    echo "[patch] RandomARQ: a injetar <stdint.h> em $UTILITY_HEADER"
    grep -qE '^[[:space:]]*#include[[:space:]]*<stdint\.h>' "$UTILITY_HEADER" \
      || sed -i '1i #include <stdint.h>' "$UTILITY_HEADER"
  fi

  mkdir build && cd build
  run cmake -DARCH=native -DCMAKE_C_FLAGS=-Wa,--noexecstack -DCMAKE_CXX_FLAGS=-Wa,--noexecstack ..
  run make
)
( cd ../Native/librandomarq && cp /tmp/RandomARQ/build/librandomx.a . && run make clean && run make )
mv ../Native/librandomarq/librandomarq.so "$OutDir"

# ---------- Panthera -> libpanthera ----------
(
  cd /tmp
  rm -rf Panthera
  git clone https://github.com/scala-network/Panthera
  cd Panthera
  git checkout cc7425f468d935ba328fba5bbb05f8227f4f22d7

  # PATCH igual ao RandomARQ: tests/utility.hpp precisa de <stdint.h>
  UTILITY_HEADER="src/tests/utility.hpp"
  if [[ -f "$UTILITY_HEADER" ]]; then
    echo "[patch] Panthera: a injetar <stdint.h> em $UTILITY_HEADER"
    grep -qE '^[[:space:]]*#include[[:space:]]*<stdint\.h>' "$UTILITY_HEADER" \
      || sed -i '1i #include <stdint.h>' "$UTILITY_HEADER"
  fi

  mkdir build && cd build
  run cmake -DARCH=native -DRANDOMX_BUILD_TESTS=OFF \
            -DCMAKE_C_FLAGS=-Wa,--noexecstack -DCMAKE_CXX_FLAGS=-Wa,--noexecstack ..
  run make
)
( cd ../Native/libpanthera && cp /tmp/Panthera/build/librandomx.a . && run make clean && run make )
mv ../Native/libpanthera/libpanthera.so "$OutDir"

# ---------- RandomXSCash -> librandomxscash ----------
(
  cd /tmp
  rm -rf RandomXSCash
  git clone https://github.com/scashnetwork/RandomX RandomXSCash
  cd RandomXSCash
  git checkout 0b3e0ded68b95491516fe974e3db784ca2742ca7
  mkdir build && cd build
  run cmake -DARCH=native -DCMAKE_C_FLAGS=-Wa,--noexecstack -DCMAKE_CXX_FLAGS=-Wa,--noexecstack ..
  run make
)
( cd ../Native/librandomxscash && cp /tmp/RandomXSCash/build/librandomx.a . && run make clean && run make )
mv ../Native/librandomxscash/librandomxscash.so "$OutDir"
