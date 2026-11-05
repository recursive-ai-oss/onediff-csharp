# Gold-set scores: aligner tie-breaks for equal and near-duplicate text (synth-v1)

Change: the block aligner settles equal and near-duplicate text by structure (`alignment.md`, steps 1–3). Same pipeline, settings, definitions and bootstrap as `synth-v1-eval-rule-grouper.md`, which is the "before" column.

Set: `synth-v1`, manifest sha256 `e11b5614d5cd967d4a3feac46e636156fb0beb0e741f96a28bfe7236c3d402a8` (eval, 210 documents, 1,050 pairs) and dev `94026ec084850e98eb9ca06d9d299773f6ffa45b9583ef43e8315922a4bbe7e2`. The aligner was tuned on dev and train (`0ee528554144f8277e0a705a9e38d48e1f35544001d9962a80fac7799d817941`) only; `check-split` finds no shared base document between eval and either. Eval was scored once, on the committed code. 10,000 bootstrap resamples over documents, seed 20261104, 95% percentile intervals. Verification passed at fallback level 0 on every pair of both splits.

"CIs" compares the new interval with the old one: *above* or *below* when they are separated, *tie* when they overlap.

| Row | Eval before | Eval now | CIs | Dev before | Dev now |
|---|---|---|---|---|---|
| align F1 same | 0.9996 [0.9994, 0.9998] | 0.9999 [0.9998, 1.0000] | above | 0.9996 [0.9991, 0.9999] | 0.9999 [0.9997, 1.0000] |
| align F1 edited | 0.9733 [0.9659, 0.9798] | 0.9793 [0.9737, 0.9844] | tie | 0.9776 [0.9622, 0.9895] | 0.9829 [0.9705, 0.9923] |
| align F1 moved | 0.9900 [0.9797, 0.9980] | 0.9880 [0.9749, 0.9979] | tie | 0.9861 [0.9518, 1.0000] | 0.9861 [0.9518, 1.0000] |
| align P moved | 0.9802 [0.9602, 0.9960] | 0.9802 [0.9604, 0.9960] | tie | 0.9861 [0.9518, 1.0000] | 0.9861 [0.9518, 1.0000] |
| align R moved | 1.0000 [1.0000, 1.0000] | 0.9960 [0.9863, 1.0000] | tie | 0.9861 [0.9518, 1.0000] | 0.9861 [0.9518, 1.0000] |
| align F1 split | 0.9880 [0.9720, 1.0000] | 0.9960 [0.9862, 1.0000] | tie | 1.0000 [1.0000, 1.0000] | 1.0000 [1.0000, 1.0000] |
| align F1 merged | 1.0000 [1.0000, 1.0000] | 1.0000 [1.0000, 1.0000] | tie | 0.9524 [0.8235, 1.0000] | 1.0000 [1.0000, 1.0000] |
| align F1 unrelated | 0.9279 [0.9121, 0.9426] | 0.9278 [0.9113, 0.9434] | tie | 0.9352 [0.9003, 0.9640] | 0.9380 [0.9004, 0.9697] |
| boundary F1 | 0.9115 [0.9007, 0.9218] | 0.9144 [0.9036, 0.9246] | tie | 0.9123 [0.8916, 0.9305] | 0.9150 [0.8943, 0.9337] |
| boundary precision | 0.9118 [0.9008, 0.9222] | 0.9135 [0.9026, 0.9238] | tie | 0.9138 [0.8936, 0.9317] | 0.9156 [0.8950, 0.9341] |
| boundary recall | 0.9112 [0.9005, 0.9213] | 0.9152 [0.9046, 0.9255] | tie | 0.9109 [0.8896, 0.9296] | 0.9144 [0.8931, 0.9335] |
| frag.sentence | 1.0047 [1.0028, 1.0070] | 1.0047 [1.0028, 1.0070] | tie | 1.0012 [1.0000, 1.0039] | 1.0012 [1.0000, 1.0039] |
| regions.sentence | 1.0037 [1.0019, 1.0057] | 1.0037 [1.0019, 1.0057] | tie | 1.0000 [1.0000, 1.0000] | 1.0000 [1.0000, 1.0000] |
| frag.unit | 1.1665 [1.1387, 1.1972] | 1.1712 [1.1423, 1.2027] | tie | 1.1245 [1.0800, 1.1780] | 1.1305 [1.0833, 1.1876] |
| regions.unit | 1.0259 [1.0194, 1.0331] | 1.0273 [1.0207, 1.0347] | tie | 1.0220 [1.0091, 1.0384] | 1.0232 [1.0101, 1.0395] |
| missed.sentence | 0.0068 [0.0038, 0.0102] | 0.0018 [0.0003, 0.0038] | tie (touching) | 0.0060 [0.0011, 0.0127] | 0.0000 [0.0000, 0.0000] |
| pairs with no difference | 8 | 8 | - | 1 | 1 |
| verify pass | 1.0000 | 1.0000 | - | 1.0000 | 1.0000 |
| verify fallback | 0.0000 | 0.0000 | - | 0.0000 | 0.0000 |

No row regresses with a separated interval. Changed sentences missed on eval: 26 → 5 (eval pairs en-049-m, zh-018-m, zh-018-s4, zh-028-m, zh-032-m). Link errors, train / dev: moved F1 0.9817 → 0.9938 / 0.9861 → 0.9861; edited 0.9753 → 0.9835 / 0.9776 → 0.9829; split 0.9855 → 1.0 / 1.0; unrelated 0.9326 → 0.9403 / 0.9352 → 0.9380; changed sentences missed 22 → 5 / 5 → 0.

## Eval per language

| Lang | boundary F1 | frag.sentence | missed.sentence | align F1 edited | align F1 moved | align F1 unrelated |
|---|---|---|---|---|---|---|
| en | 0.9192 [0.8925, 0.9427] | 1.0019 [1.0000, 1.0058] | 0.0038 [0.0000, 0.0121] | 0.9750 [0.9608, 0.9868] | 0.9744 [0.8919, 1.0000] | 0.9091 [0.8636, 0.9461] |
| de | 0.9244 [0.9033, 0.9447] | 1.0148 [1.0059, 1.0263] | 0.0000 [0.0000, 0.0000] | 0.9751 [0.9524, 0.9907] | 1.0000 [1.0000, 1.0000] | 0.9207 [0.8772, 0.9643] |
| fr | 0.9239 [0.9083, 0.9373] | 1.0037 [1.0000, 1.0098] | 0.0000 [0.0000, 0.0000] | 0.9842 [0.9671, 0.9958] | 0.9880 [0.9524, 1.0000] | 0.9494 [0.8929, 0.9860] |
| es | 0.9472 [0.9236, 0.9659] | 1.0036 [1.0000, 1.0093] | 0.0000 [0.0000, 0.0000] | 0.9812 [0.9664, 0.9925] | 0.9825 [0.9302, 1.0000] | 0.9324 [0.8800, 0.9708] |
| zh | 0.8941 [0.8564, 0.9275] | 1.0054 [1.0000, 1.0123] | 0.0089 [0.0000, 0.0207] | 0.9787 [0.9633, 0.9913] | 0.9841 [0.9302, 1.0000] | 0.9226 [0.8819, 0.9630] |
| ja | 0.8807 [0.8377, 0.9187] | 1.0017 [1.0000, 1.0056] | 0.0000 [0.0000, 0.0000] | 0.9805 [0.9669, 0.9904] | 1.0000 [1.0000, 1.0000] | 0.9319 [0.8953, 0.9641] |
| ar | 0.9164 [0.8998, 0.9343] | 1.0035 [1.0000, 1.0089] | 0.0000 [0.0000, 0.0000] | 0.9801 [0.9619, 0.9921] | 0.9863 [0.9434, 1.0000] | 0.9282 [0.8785, 0.9668] |

## What is left

- A sentence repeated inside one paragraph: deleting either copy gives the same text, and the gold labels delete the first copy in 4 of 6 cases and the last in 2 (train and dev). No rule matches that, so none was added.
- A table row replaced by a row with an equal first cell: the labels say delete and insert, the aligner says the row's other cells were edited.
