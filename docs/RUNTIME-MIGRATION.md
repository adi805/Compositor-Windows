# Migrasi runtime dan reproduksibilitas rilis

Menutup #9. Dokumen ini mencatat keputusan yang sudah diambil dan sisa pekerjaan migrasinya.

## Keadaan sekarang

| Hal | Nilai | Di mana |
|---|---|---|
| Target framework | `net8.0` | `Directory.Build.props` |
| SDK | dipin ke 8.0, `rollForward: latestFeature` | `global.json` |
| Graf paket | dikunci per proyek | `packages.lock.json` |
| Restore di CI | `--locked-mode` | `ci.yml`, `release.yml` |
| Action GitHub | dipin ke commit SHA | ketiga workflow |
| Rilis | self-contained `win-x64` | `release.yml` |

## Kenapa `net8.0` masih dipakai, dan sampai kapan

.NET 8 habis masa dukung **2026-11-10**; .NET 10 adalah LTS sampai 2028-11-14. Rilis kita
self-contained, jadi aplikasi yang sudah terpasang tetap jalan setelah tanggal itu: yang berhenti
adalah patch keamanan. Untuk pre-alpha itu tidak fatal, tapi v1.0 yang dibagikan ke orang lain tidak
boleh keluar di atas runtime tanpa dukungan.

Rencananya: **migrasi `net8.0` → `net10.0` sebelum v1.0**, bukan sesudah.

Yang perlu disentuh, dari pengalaman struktur repo ini:

1. `TargetFramework` di `Directory.Build.props`, dan `LangVersion` (12 → 14) kalau mau fitur baru.
2. `global.json`: `version` ke `10.0.100`, `rollForward` tetap `latestFeature`.
3. `LangVersion` naik berarti analyzer `latest-recommended` ikut naik: jalankan build bersih tanpa
   `obj/` dan `bin/` dulu, karena build inkremental tidak menjalankan ulang analyzer dan itu sudah
   dua kali membuat CI merah mendadak.
4. `packages.lock.json` di-regenerate untuk kelima proyek.
5. `dotnet-version` di workflow: setup-dotnet menerima `10.0.x`.
6. Cek ulang `Avalonia` dan `SkiaSharp` transitif: kenaikan mayor runtime kadang memaksa kenaikan
   paket, dan SkiaSharp adalah native asset yang harus cocok dengan `win-x64` yang di-publish.
7. `Smoke.Run` harus lulus di biner hasil publish sebelum rilis dianggap sah; itu sudah jadi bagian
   dari gate rilis, jadi migrasi yang merusak codec native akan ketahuan di situ, bukan di laporan bug.

ONNX Runtime test-only (`Microsoft.ML.OnnxRuntime` ada di `tests/Compositor.App.Tests`). Itu bukan
dependensi aplikasi yang dikirim, dan jangan dihitung sebagai beban rilis.

## Kenapa SDK dipin di major.minor, bukan di patch

`global.json` memakai `"version": "8.0.100", "rollForward": "latestFeature"`. Artinya: SDK 8.0.x apa
pun diterima, 9.x atau 10.x ditolak.

Pin ke nomor patch tertentu tidak berguna di sini karena angka SDK berbeda antara distribusi.
Ubuntu mengemas SDK sebagai `8.0.131`, sedangkan Microsoft menerbitkan band `8.0.4xx` (`8.0.425`
saat dokumen ini ditulis). Mem-pin `8.0.131` akan menggagalkan setiap runner CI, karena setup-dotnet
mengunduh dari indeks Microsoft dan versi itu tidak ada di sana. Yang benar-benar perlu dijaga adalah
major.minor, karena itu yang mengubah perilaku compiler dan analyzer.

Kontrol negatifnya: `global.json` berisi `10.0.100` di mesin ini menghasilkan
`A compatible .NET SDK was not found`. Pin-nya bekerja.

## Kenapa lock file, dan cara mengubahnya

Tanpa lock file, graf dependensi adalah apa pun yang feed sajikan hari itu: paket transitif bisa
diterbitkan ulang, rentang versi bisa resolve berbeda, dan build yang menghasilkan sebuah rilis tidak
bisa direproduksi dari commit-nya. `RestorePackagesWithLockFile` menulis `packages.lock.json` per
proyek, dan CI menjalankan `dotnet restore --locked-mode` sehingga restore **gagal** kalau grafnya
tidak cocok dengan yang tercatat.

Kontrol negatifnya sudah dijalankan: menambah satu `PackageReference` tanpa memperbarui lock file
menghasilkan `error NU1004: The project reference compositor.core has changed`.

Mengubah versi paket berarti regenerasi yang disengaja:

```bash
dotnet restore            # menulis ulang packages.lock.json
git add "*/packages.lock.json"
```

Lock file yang berubah tanpa perubahan paket yang jelas adalah sinyal untuk diperiksa, bukan untuk
di-commit.

## Action dipin ke SHA

`actions/checkout@v4` dan `actions/setup-dotnet@v4` adalah tag bergerak. Saat dokumen ini ditulis,
`v4` dari checkout menunjuk `11d5960a…` (commit 2026-07-16), bukan `11bd7190…` yang dulu dipakai
banyak orang untuk v4.2.2: tag-nya memang sudah bergerak, dan itu tepat alasan mem-pin ke commit.
Ketiga workflow sekarang memakai SHA penuh dengan komentar versinya, sehingga pembaruan action
adalah perubahan yang terlihat di diff.
