# Menandatangani rilis

Updater memverifikasi dua hal yang berbeda, dan keduanya perlu.

| Pemeriksaan | Pertanyaan yang dijawab | Sumber |
|---|---|---|
| Checksum (`SHA256SUMS`) | Apakah unduhannya utuh? | Rilis yang sama dengan paketnya |
| Tanda tangan (`SHA256SUMS.sig`) | Siapa yang menerbitkannya? | Kunci yang ditanam di dalam aplikasi |

Checksum saja tidak cukup: siapa pun yang bisa menerbitkan rilis bisa mengganti zip **dan**
`SHA256SUMS` sekaligus, dan hasilnya tetap konsisten. Yang tidak bisa dia hasilkan adalah tanda
tangan dengan kunci yang tidak dia punya.

## Sekali saja: bikin kunci

```bash
openssl genrsa -out update.key 4096
openssl rsa -in update.key -pubout -out update.pub
```

Simpan **private key** (`update.key`) di luar repo, lalu:

1. GitHub → Settings → Secrets and variables → Actions → **Secrets**: tambah `UPDATE_SIGNING_KEY`,
   isinya seluruh isi `update.key` (termasuk baris `BEGIN`/`END`).
2. GitHub → halaman yang sama → **Variables**: tambah `UPDATE_PUBLIC_KEY`, isinya seluruh isi
   `update.pub`.

`UPDATE_SIGNING_KEY` menandatangani; `UPDATE_PUBLIC_KEY` ditanam ke dalam biner saat build. Dua
nama berbeda karena yang satu rahasia dan yang satu memang publik, dan tertukar di antara keduanya
akan terlihat langsung: rilis gagal, bukan diam-diam tidak terverifikasi.

## Tiap rilis

`release.yml` melakukan ini sendiri:

```bash
sha256sum "Compositor-Windows-${GITHUB_REF_NAME}-win-x64.zip" > SHA256SUMS
openssl rsa -in signing.key -pubout -out signing.pub
openssl dgst -sha256 -sign signing.key -out SHA256SUMS.sig SHA256SUMS
openssl dgst -sha256 -verify signing.pub -signature SHA256SUMS.sig SHA256SUMS
```

Baris terakhir itu penting: kunci yang salah ketik ketahuan saat rilis, bukan saat update pertama
user ditolak.

Rilis **gagal** kalau `UPDATE_SIGNING_KEY` atau `UPDATE_PUBLIC_KEY` belum diset. Itu disengaja:
tanpa kunci, biner yang diterbitkan tidak bisa mengautentikasi update-nya sendiri, dan lebih baik
gagal di CI daripada menerbitkan rilis yang tiap update-nya ditolak.

## Bagaimana aplikasi memutuskan

`UpdateTrust.Check` di `src/Compositor.App/Update/UpdateTrust.cs`:

| Kondisi build | Hasil |
|---|---|
| Kunci ditanam, tanda tangan sah | Diterima, alasan menyebut kunci mana |
| Kunci ditanam, tanda tangan tidak ada | **Ditolak**: rilis tidak membawa tanda tangan |
| Kunci ditanam, tanda tangan tidak cocok | **Ditolak**: bukan kunci ini yang menandatangani |
| Kunci ditanam, PEM tidak bisa dibaca | Diterima sebagai "tanpa kunci", dan mengatakannya |
| Tidak ada kunci | Diterima, alasan menyatakan ini hanya membuktikan unduhan utuh |

Baris terakhir itu keadaan default sebuah build biasa, dan disengaja: perbedaan antara "diperiksa
dan lolos" dengan "tidak bisa diperiksa" harus terlihat, bukan dilebur. Yang berbahaya bukan
verifikasi yang gagal, melainkan verifikasi yang tidak pernah mungkin dan tidak ada yang sadar.

## Rotasi kunci

Isi `UPDATE_PUBLIC_KEY` dengan **dua** blok PEM (kunci lama lalu kunci baru). Keduanya dipercaya,
jadi rilis bisa ditandatangani dengan salah satunya selama masa transisi. Setelah semua klien
terbarui, buang blok lama.

Kunci dikirim ke biner lewat `-p:CompositorUpdatePublicKey=...` dan mendarat di assembly metadata;
`UpdateTrust` membacanya dari sana. Kunci tidak pernah diunduh saat runtime, karena kunci yang
datang lewat kanal yang sama dengan paketnya tidak mengautentikasi apa pun.

## Membuktikan rantainya

`UpdateSignatureInteropTests` menyimpan sepasang fixture yang ditandatangani `openssl` sungguhan,
lengkap dengan kunci publiknya. Tes itu memastikan verifier .NET menerima tanda tangan yang dibuat
`openssl`, karena unit test yang menandatangani dengan `RSA.SignData` hanya membuktikan verifiernya
konsisten dengan dirinya sendiri.
