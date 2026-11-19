# 微信 SILK 协议样本

这些文件由本次验证生成，不包含真实微信账号数据或第三方录音。原始信号为 440/880 Hz、带淡入淡出的单声道正弦波，分别以 8/12/16/24 kHz 输入 `silk-wasm@3.7.1` 编码。所有 `.silk` 文件使用微信容器：`0x02` + `#!SILK_V3` + 小端长度前缀数据包，而非只测试标准 SILK 文件。

`.native.pcm` 为 SILK SDK 1.0.9 的 `SKP_Silk_SDK_Decode` 逐包输出，采样率 24 kHz、单声道、PCM16 little-endian。参考源码来自 [silk-wasm](https://github.com/idranme/silk-wasm)，提交 `2793875764ad98245c177ac496eb5b7100a71038` 下 `binding/libSilkCodec/silk/src/SILK_SDK_SRC_ARM_v1.0.9`；生成探针记录在本次 artifacts/reference/decoder_probe.c。测试运行不依赖该 C SDK、WASM 或 Node。

24 kHz 样本要求 DrAbc.SilkCodec 1.0.0 的 PCM 与参考文件逐字节相同。8/12/16 kHz 内部采样需要重采样到 24 kHz；由于重采样器延迟不同，测试先在 ±100 samples 范围对齐，再要求相关性 > 0.999，并检查准确输出长度。不能用 `silk-wasm` JavaScript 包装层直接输出的 PCM 作为逐字节 golden，其输出与底层 SDK 的逐包解码不同。

每次测试还检查微信 `0x02` 前缀、魔数、所有分包边界与完整尾部，并测试截断、非法长度和尾部垃圾回退原始 SILK。生成样本不能替代真机语音验收，详见 [SILK 验证记录](../../../docs/silk-validation.md)。

| 文件 | SHA-256 |
| --- | --- |
| tone-8000.silk | af2c7d3bbfe9bb99409f5de7c8a681e043e587d8b889933e396b971479d6370d |
| tone-8000.native.pcm | 65145490fc9180794ff9419fa7ef09abd0a5d0a5a4cc2d7ddd834715fa000fcb |
| tone-12000.silk | 4fb4719ec12664ed78a7071d30514819ac5d780ad988130f626250966fc3f83a |
| tone-12000.native.pcm | 66ae283db0fcca8f0b44ca3fe0c99b124a83b5d32447c33b4d85cc816f8d0405 |
| tone-16000.silk | 1d48eb1b26dbff35da83aadad4b590c139bda0f923b0adf8ca678f0868008c46 |
| tone-16000.native.pcm | eedeb0f293572c273fd51b8e1d7d80160c2ce61de5a64b6b334656f97f2f8fb3 |
| tone-24000.silk | d401d90708ad4bdd7880403a43264220d4401fa553962e4ea92ea2e88606ccfd |
| tone-24000.native.pcm | 805f820127a4f62e0d0603cd3f4399779fb09ad4950db70a66438022ca591c7d |
