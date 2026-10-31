whisper.cpp (ggml-org/whisper.cpp), CPU build for Windows x64: release b5454, whisper-bin-x64.zip
https://github.com/ggml-org/whisper.cpp/releases/tag/b5454
Licensed under the MIT License (see LICENSE).

Only whisper-cli.exe and the DLLs it loads are included (whisper.dll, ggml.dll, ggml-base.dll and the
ggml-cpu-*.dll variants it picks from at run time). It needs the Microsoft Visual C++ Redistributable (x64).
Speech models are not included: they are downloaded from
https://huggingface.co/ggerganov/whisper.cpp into the app's data folder (Speech to Text > Model).
