# Лицензии зависимостей

Лицензии и уведомления распространяются вместе с Release. Выбранные лицензии разрешают распространение соответствующих бинарных компонентов без обязательного раскрытия исходного кода Drawing Renamer при соблюдении их условий.

| Компонент | Лицензия и происхождение |
|---|---|
| PdfPig 0.1.16 | Apache-2.0. [Исходники и лицензия](https://github.com/UglyToad/PdfPig/tree/a7bb35662bbbf405efddad50aedc9bcdcf515afc) |
| PDFium 157.0.8100, сборка bblanchon | PDFium распространяется по BSD-3-Clause. Инфраструктура сборки — Apache-2.0. Каталог `pdfium` содержит полный набор лицензий из архива сборки, включая библиотеки Chromium, шрифтов и изображений. [Архив сборки](https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium%2F8100) |
| Языковые модели PP-OCRv5 `en`, `eslav` | Apache-2.0, © PaddlePaddle Authors. Модели преобразованы в ONNX проектом RapidOCR, каталог v3.10.0. [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) · [Происхождение моделей и контрольные суммы](https://rapidai.github.io/RapidOCRDocs/main/en/model_licenses/) |
| ONNX Runtime 1.24.4 | MIT. Уведомления для сторонних компонентов включены из NuGet. [Пакет](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/1.24.4) |
| CommunityToolkit.Mvvm 8.4.2 и .NET | MIT. Лицензионные уведомления взяты из пакетов. [CommunityToolkit](https://github.com/CommunityToolkit/dotnet), [.NET](https://github.com/dotnet/runtime) |
| Windows App SDK 2.6.5 | Microsoft Software License Terms. Раздел 3 разрешает распространять файлы, скопированные NuGet в приложение, с указанными там условиями. [Пакет runtime](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Runtime/2.6.5) |
| WebView2 SDK 1.0.3719.77 | Лицензия и уведомления Microsoft из транзитивного пакета. Приложение не создаёт WebView. [Пакет](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77) |
| Visual C++ Runtime v143 x64 | App-local DLL из каталога `VC\Redist` Visual Studio. [Условия распространения](https://learn.microsoft.com/visualstudio/releases/2022/redistribution#visual-c-runtime-files) |

Windows SDK BuildTools, MSIX BuildTools и зависимости xUnit используются при сборке или тестировании. Точный граф зависимостей закреплён в `packages.lock.json` каждого проекта.
