# Archive test fixtures

This directory contains only small archives exercised by ReachCommander tests. It contains no personal media, credentials, or user content. `SHA256SUMS` is the machine-readable hash list.

## ReachCommander-owned fixtures

Run `./generate-safe-fixtures.ps1` from any working directory to rebuild `nested.zip`, `sample.7z`, and `split.zip.001`–`.003`. The script builds against the pinned SharpCompress 0.50.4 package, uses `System.IO.Compression.ZipArchive` and `SharpCompress.Writers.SevenZip.SevenZipWriter`, sets every timestamp to `2000-01-01T00:00:00Z`, and writes these exact UTF-8 payloads:

- `root.txt` — 13 bytes — `root fixture\n`
- `Family/2025/photo.txt` — 14 bytes — `photo fixture\n`
- `Family/2025/nested.zip` — 22 bytes — `nested archive marker\n`; this is deliberately an ordinary nested-archive marker, not a browsable child archive

The numbered ZIP parts are contiguous byte ranges of `nested.zip`; concatenating `.001`, `.002`, and `.003` in that order reproduces it exactly.

| File | Format and purpose | SHA-256 | Expected catalog | Tests |
|---|---|---|---|---|
| `nested.zip` | Generated single ZIP; browser browse/extract fixture | `e01f13e2afe5550e950de983e458d607043b74fac6e2c65d97ef26f8d9458da3` | The three files above | `ArchiveWorkerInspectionTests`, `ArchiveWorkerExtractionTests`, `archive-workflow.spec.ts` |
| `sample.7z` | Generated single 7z compatibility fixture | `d77ab06c4c2c680d49b9d051a3266fb9ba6d4d93b084d13d03bd3a2e4c3f7469` | The three files above | `ArchiveWorkerInspectionTests` |
| `split.zip.001` | Generated raw split ZIP, part 1/3 | `1bdb3df642d2e5cccd00f97c6fd0b4142a34dd49404f16ce599af7feea77f9fe` | Complete set: the three files above | `ArchiveWorkerInspectionTests`, `ArchiveWorkerExtractionTests` |
| `split.zip.002` | Generated raw split ZIP, part 2/3 | `fe0115ce0d3d0b44d6476dd3b875a1235863f8c9bfc61ecb928a41d130b8b969` | Complete set: the three files above | `ArchiveWorkerInspectionTests`, `ArchiveWorkerExtractionTests` |
| `split.zip.003` | Generated raw split ZIP, part 3/3 | `d898ecb41379fbadc592a4fc9dc615087b2f79383452423401e93f9dfb718a25` | Complete set: the three files above | `ArchiveWorkerInspectionTests`, `ArchiveWorkerExtractionTests` |

## SharpCompress compatibility fixtures

The remaining binaries are unchanged copies from SharpCompress tag `0.50.4`, peeled commit `c083c6efd843a844b0c8f7878787360e815be781`, under `tests/TestArchives/Archives/`. Each filename links to its immutable raw source. SharpCompress and its test corpus are distributed under the [MIT License](https://github.com/adamhathcock/sharpcompress/blob/c083c6efd843a844b0c8f7878787360e815be781/LICENSE.txt).

Catalog keys used below:

- **RAR common:** `тест.txt` 15,498; `exe/test.exe` 45,056; `jpg/test.jpg` 40,372; directory records vary by RAR variant and have size 0.
- **7z common:** `тест.txt` 15,498; `exe/test.exe` 45,056; `jpg/test.jpg` 40,372; `exe` and `jpg` directories size 0.
- **Original 7z:** the common files beneath `Original/`, plus `Original`, `Original/exe`, and `Original/jpg` directories size 0.
- **Classic ZIP:** `тест.txt` 15,498; `exe/test.exe` 45,056; `jpg/test.jpg` 40,372; `exe` and `jpg` directories size 0.

| File and immutable source | Format and purpose | SHA-256 | Expected entries/sizes | Tests |
|---|---|---|---|---|
| [`7Zip.encryptedFiles.7z`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/7Zip.encryptedFiles.7z) | 7z; encrypted-entry rejection | `54dada890893c9abe8d8b06bc446d66c288fa667cb51de919c2e3216d3c6de34` | 7z common; all three files encrypted | `ArchiveWorkerInspectionTests` |
| [`7Zip.nonsolid.7z`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/7Zip.nonsolid.7z) | 7z; non-solid single volume | `221db581cb21336219963acb1cd9f9f9760bd7ae1dec112e3c0c68eb1791354d` | 7z common | `ArchiveWorkerInspectionTests` |
| [`7Zip.Tar.tar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/7Zip.Tar.tar) | TAR; unsupported-signature rejection | `ca84544a8472bdf28aeea304e204fce45801cc9ad046ac5f63d16595015a4755` | `7Zip.Tar/` 0; `7Zip.Tar/test.txt` 4 | `ArchiveWorkerInspectionTests` |
| [`Infozip.nocomp.multi.z01`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Infozip.nocomp.multi.z01) | Classic split ZIP part 1/2 | `96fb0ed91d3e7f90f05b80ca86e8665579431f5bf54027a0449f7834499da78e` | Complete set: Classic ZIP | `ArchiveWorkerInspectionTests` |
| [`Infozip.nocomp.multi.zip`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Infozip.nocomp.multi.zip) | Classic split ZIP primary part 2/2 | `7c4820ba3e5c9be70aa46a37ab3b5306dbb414bb9f1543b8c00c6229681510b4` | Complete set: Classic ZIP | `ArchiveWorkerInspectionTests` |
| [`Original.7z.001`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Original.7z.001) | Numbered 7z part 1/7, primary | `ad8afdd38d1fc1cf15a8b698317a521c24aaf0176540d52dae85422f7c8104e5` | Complete set: Original 7z | `ArchiveWorkerInspectionTests` |
| [`Original.7z.002`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Original.7z.002) | Numbered 7z part 2/7 | `2476812714eea5ec28b94cdbe75d0d349313e081bea9b82624d5e4dc63f5b735` | Complete set: Original 7z | `ArchiveWorkerInspectionTests` |
| [`Original.7z.003`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Original.7z.003) | Numbered 7z part 3/7 | `ce7bbb4770bbd284307e15123427812260d8f8f13fbb8aff20cbc34952f8279c` | Complete set: Original 7z | `ArchiveWorkerInspectionTests` |
| [`Original.7z.004`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Original.7z.004) | Numbered 7z part 4/7 | `4f0c03fefb01f02de20a6b8ac6417d609b3b59f96195b31e8b641afb504d84c3` | Complete set: Original 7z | `ArchiveWorkerInspectionTests` |
| [`Original.7z.005`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Original.7z.005) | Numbered 7z part 5/7 | `1ed608541549360f3909b94579d6f96b5f1891e6649798245fc72eab9e3ba10d` | Complete set: Original 7z | `ArchiveWorkerInspectionTests` |
| [`Original.7z.006`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Original.7z.006) | Numbered 7z part 6/7 | `443be41814398f97d14ef535a2bc36366d3a9a606dd1cca5cc60c28c37a1dfeb` | Complete set: Original 7z | `ArchiveWorkerInspectionTests` |
| [`Original.7z.007`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Original.7z.007) | Numbered 7z part 7/7 | `eca73eb36f830c55194fbf0cfe4892c50ca7dc85aab326dadb1a2b30bb1931ee` | Complete set: Original 7z | `ArchiveWorkerInspectionTests` |
| [`Rar.encrypted_filesOnly.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.encrypted_filesOnly.rar) | RAR; encrypted-entry rejection | `52c0d575e01750ae643b65866b5e06e67773a0aed5edea6b2b04e7de14a784a2` | RAR common; all three files encrypted | `ArchiveWorkerInspectionTests` |
| [`Rar.malformed_512byte.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.malformed_512byte.rar) | RAR; deterministic malformed-input rejection | `7d915245989596f6cd080151fa350b54f0551797c26681639fbd856d6cc16088` | No valid catalog | `ArchiveWorkerInspectionTests` |
| [`Rar.multi.part01.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.multi.part01.rar) | Modern RAR part 1/6, primary | `6b431db9f9d4ef45b3c4d9a15baf760d3ba1ca8896c7c13c0ba47041c9816cf7` | Complete set: RAR common | `ArchiveWorkerInspectionTests`, `archive-workflow.spec.ts` |
| [`Rar.multi.part02.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.multi.part02.rar) | Modern RAR part 2/6; secondary guidance | `8211c7b2389c8c722bae7d535a7b71a5da90495d417e73afc33adc42511f95f6` | Complete set: RAR common | `ArchiveWorkerInspectionTests`, `archive-workflow.spec.ts` |
| [`Rar.multi.part03.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.multi.part03.rar) | Modern RAR part 3/6 | `473a598bed36b0371b21a7bde91d2d4af64d2e8ca35d8ddd4a344ca0a76cba04` | Complete set: RAR common | `ArchiveWorkerInspectionTests`, `archive-workflow.spec.ts` |
| [`Rar.multi.part04.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.multi.part04.rar) | Modern RAR part 4/6 | `3c692b219e8c33c4eb38881f1d4c5a97e98302e164f0cf17318027de7b17f619` | Complete set: RAR common | `ArchiveWorkerInspectionTests`, `archive-workflow.spec.ts` |
| [`Rar.multi.part05.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.multi.part05.rar) | Modern RAR part 5/6 | `49112d1702e61e52608fa3461f273a160159ff1db142f48fa0e9f84445fc46e5` | Complete set: RAR common | `ArchiveWorkerInspectionTests`, `archive-workflow.spec.ts` |
| [`Rar.multi.part06.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.multi.part06.rar) | Modern RAR part 6/6 | `1e1dd645b107c6f620672efb1e50dbf03d897df759f79f664dad3b962460251a` | Complete set: RAR common | `ArchiveWorkerInspectionTests`, `archive-workflow.spec.ts` |
| [`Rar.multi.solid.part02.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.multi.solid.part02.rar) | Foreign RAR part injected into another set | `28257cb924d52518cc1192b9bbe222237b14e3137b829936a05c64b0a30242f5` | No standalone catalog; must invalidate mixed set | `ArchiveWorkerInspectionTests` |
| [`Rar.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.rar) | RAR; single non-solid volume | `60db161de57dc59aa12e0c45b1b70d78904da3d104e74748972ac38643f12802` | RAR common plus `Empty`, `exe`, `jpg` directories | `ArchiveWorkerInspectionTests` |
| [`Rar.solid.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.solid.rar) | RAR; solid selection/extraction | `e7e62f24f22f195d21437be09667c8956837d99ba48d7270090251a8e32babc5` | RAR common plus `Empty`, `exe`, `jpg` directories | `ArchiveWorkerInspectionTests`, `ArchiveWorkerExtractionTests` |
| [`Rar2.multi.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar2.multi.rar) | Legacy RAR primary part 1/7 | `bc8dc02828e7552e4a0689e9476a13391f9896b872659d846de902fd51e5790b` | Complete set: RAR common | `ArchiveWorkerInspectionTests` |
| [`Rar2.multi.r00`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar2.multi.r00) | Legacy RAR part 2/7 | `1f506b6e5518dda165de63a9c9fea4432ee1a6442fbdffeb24be5522ea21c4a6` | Complete set: RAR common | `ArchiveWorkerInspectionTests` |
| [`Rar2.multi.r01`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar2.multi.r01) | Legacy RAR part 3/7 | `270d317f092b0e7b8513ecc0a055d8cb703d91e86e927f91917e9b75c760942d` | Complete set: RAR common | `ArchiveWorkerInspectionTests` |
| [`Rar2.multi.r02`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar2.multi.r02) | Legacy RAR part 4/7 | `e010f1008bf415d3c31ce81fa859f979740d0b92e2086d524884bbc2c4104589` | Complete set: RAR common | `ArchiveWorkerInspectionTests` |
| [`Rar2.multi.r03`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar2.multi.r03) | Legacy RAR part 5/7 | `08f1927004b333789a4da356012eddc7e33bede968a9a3207af03990470f03fc` | Complete set: RAR common | `ArchiveWorkerInspectionTests` |
| [`Rar2.multi.r04`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar2.multi.r04) | Legacy RAR part 6/7 | `b6d0f9ecca5f96c0cc72e7cb94b8e806083eb08616bbab3c5d42f1c36e70f9bf` | Complete set: RAR common | `ArchiveWorkerInspectionTests` |
| [`Rar2.multi.r05`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar2.multi.r05) | Legacy RAR part 7/7 | `e8a358cfb78115f4ff56e88d0959f46e01e6680eb5d14b805b92fc54b8c3a30d` | Complete set: RAR common | `ArchiveWorkerInspectionTests` |
| [`Zip.deflate.zip`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Zip.deflate.zip) | ZIP; deflate single volume and limits | `78720af73face20069dcabda43589095b439f236531c6268f8301028dbb65f9c` | Classic ZIP plus `Empty` directory | `ArchiveWorkerInspectionTests` |
| [`Zip.none.encrypted.zip`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Zip.none.encrypted.zip) | ZIP; encrypted rejection during inspect/extract | `3fa1b60719a807f6f3eabd14dedca1b21e0c03df24d0a36103d20b876a85f9c8` | encrypted `first.txt` 199; `second.txt` 197 | `ArchiveWorkerInspectionTests`, `ArchiveWorkerExtractionTests` |

Verify all committed binaries from PowerShell:

```powershell
Get-Content ./SHA256SUMS | ForEach-Object {
    $hash, $name = $_ -split '\s+', 2
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $name.Trim()).Hash -ne $hash) {
        throw "Fixture hash mismatch: $name"
    }
}
```
