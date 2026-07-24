# Stage 5 third-party development dependencies

Stage 5 validates these dependencies from ignored local paths. None of the
downloaded source trees, models, native runtime binaries or generated fixtures
is distributed or committed by this change.

| Dependency | Exact pin | License | Stage 5 use | Local binary/model impact |
|---|---|---|---|---|
| whisper.cpp | v1.8.6, commit `23ee03506a91ac3d3f0071b40e66a430eebdfa1d` | MIT | CPU transcription adapter; statically linked | Local checkout about 35.96 MB; official multilingual `ggml-tiny.bin` 77,691,713 bytes (SHA-256 `BE07E048E1E599AD46341C8D2A135645097A538221678B7ACDD1B1919C6E1B21`) |
| Silero VAD | v6.2.1, commit `7e30209a3e901f9842f81b225f3e93d8199902b1` | MIT | streaming speech gate | `silero_vad_16k_op15.onnx` 1,289,603 bytes; SHA-256 `7ED98DDBAD84CCAC4CD0AEB3099049280713DF825C610A8ED34543318F1B2C49` |
| ONNX Runtime | 1.23.0, tag commit `be835efc56aca19b8e810538ec93c8e150e0fc61` | MIT | official Windows x64 CPU execution runtime for Silero | Extracted SDK about 393.27 MB including symbols; deployed `onnxruntime.dll` 14,107,168 bytes plus import library |

The pinned Silero model uses opset 15. It exposes inputs `input` float
`[batch,samples]`, `state` float `[2,batch,128]`, and scalar int64 `sr`; outputs
are `output` and `stateN`. At 16 kHz the adapter supplies 512 new samples plus
64 samples of bounded context as a 576-sample tensor, carries recurrent state,
and resets both on stream/gap boundaries. The worker validates this metadata
before readiness.

The official ONNX Runtime Windows x64 CPU archive supplies headers under
`include` and `onnxruntime.lib`/`onnxruntime.dll` under `lib`. CMake copies only
the DLL beside the local worker and never mutates system `PATH`. These pins
support MSVC and the Windows 10 x64 target used for validation. Packaging,
redistribution, automatic model download and CUDA/GPU binaries are deliberately
not included yet.
