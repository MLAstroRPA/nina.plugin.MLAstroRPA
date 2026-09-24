# mlastro-iconbuild

Chuyển đổi một SVG **đơn sắc** (gồm các `<path>` + một phần tử `<text>`) thành file
XAML `GeometryGroup` **1 màu** dùng làm **dockable icon** trong NINA
(file đích: `Resources\MLAstroTPPAIcon.xaml`, key `MLAstroTPPAIcon`).

Text trong SVG (ví dụ chữ **RPA**) được **vector hóa thành glyph outline** (path)
thông qua WPF `FormattedText`, vì NINA chỉ chấp nhận geometry thuần (không có text).

## Yêu cầu

- Windows
- .NET SDK (đã dùng `net10.0-windows` + WPF)
- Font dùng trong SVG phải có trên máy (ví dụ `Arial` / `Arial Black`)

## Cách dùng

```powershell
dotnet run --project tools\mlastro-iconbuild -- `
  three_stars.svg `
  Resources\MLAstroTPPAIcon.xaml `
  preview.png
```

- `three_stars.svg` — SVG nguồn (3 ngôi sao viền + chữ RPA, fill đơn sắc).
- `Resources\MLAstroTPPAIcon.xaml` — file XAML đích (tự động ghi đè).
- `preview.png` — (tùy chọn) ảnh xem trước để đối chiếu trước khi build NINA.

## Lưu ý kỹ thuật

- Fill-rule: chương trình dùng `Nonzero` cho cả `GeometryGroup` lẫn từng `PathGeometry`
  để tái hiện đúng `fill-rule:nonzero` của SVG (các ngôi sao "viền" = 2 subpath ngược
  chiều tạo lỗ).
- Chữ được canh giữa theo `text-anchor:middle` + `dominant-baseline:central` tại
  `x,y` của thẻ `<text>`.
- Sau khi sinh XAML, hãy nhìn `preview.png` để kiểm tra tỉ lệ/độ đậm của chữ (glyph
  WPF có thể hơi khác SVG một chút do font metric).
- Sau đó chạy build plugin để NINA nạp icon mới:
  `dotnet build MLAstroRPA_TPPA.csproj -c Release -tl:off`
