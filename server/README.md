# VRPhoto — SHARP PLY library for PICO 4

The native Unity Pico viewer and this server now live in the same repository. The
native viewer reads `GET /api/v1/library?path=<folder>&page=<number>`. The response
contains relative folder paths, scene paths, preview and PLY links, coordinate
systems, and page information. The browser viewer described below remains available.
The server prints the SHA-256 fingerprint of its leaf HTTPS certificate at startup;
compare it with the native viewer's first-connect prompt. The native viewer does not
require installing the CA certificate on the headset.

Локальный Python CLI с двумя независимыми командами: `process` импортирует фотографии через уже установленный Apple SHARP, `serve` показывает библиотеку через HTTPS. Основной viewer использует Spark 2.2.0 и Three.js 0.180.0. Их модули включены в пакет: внешнего CDN или Node.js при запуске не требуется. Предыдущий PlayCanvas viewer доступен по `?engine=playcanvas` для сравнения.

## Установка на Mac

```bash
cd /path/to/vrphoto
python3 -m venv .venv
.venv/bin/python -m pip install -e .
.venv/bin/vrphoto --help
```

SHARP остаётся в своём окружении `/Users/artanis/WORK/GIT/sharp`. VRPhoto ищет `bin/sharp` в указанной директории или внутри `venv` / `.venv` / `env`. Если путь иной, укажите точный исполняемый файл через `--sharp-path`.

## Первый PICO PoC с уже существующим PLY

Папку с готовыми `scene.ply` и `preview.jpg` можно просто положить внутрь библиотеки. `metadata.json` для просмотра не нужен. Если он создан нашим импортом SHARP, viewer использует сохранённую систему координат фотографии; остальные PLY автоматически помещает перед зрителем по границам модели.

```bash
.venv/bin/vrphoto add-ply /path/to/scene.ply /path/to/source-photo.jpg /path/to/VRLibrary
.venv/bin/vrphoto serve /path/to/VRLibrary --host 0.0.0.0 --port 8443
```

Откройте показанный в логе `https://<LAN-IP>:8443/` на компьютере и PICO 4. Сервер при первом запуске создаёт локальный CA и серверный сертификат в `~/.local/share/vrphoto/`. Установите **только** `local-ca.crt` как доверенный CA на PICO согласно настройкам устройства; приватные `.key` файлы не переносите. Сертификат содержит LAN IP, показанный при запуске. После смены IP сервер перевыпустит leaf-сертификат с новым SAN, сохранив тот же CA. Браузер должен показывать доверенное HTTPS-соединение до проверки WebXR.

Откройте `/debug/capabilities` **в PICO Browser**. На странице сцены нажмите `Enter VR` и проверьте движение головы по X/Y/Z и yaw/pitch/roll. Начальный бюджет — `Full` (100% splats); `Low`, `Medium`, `High` выбираются вручную. Основной viewer использует WebGL2. Страница capabilities также показывает старые диагностические проверки PlayCanvas для сравнения.

При `Ctrl+C` сервер закрывает listener и выходит. Обычный HTTP, отправленный на порт 8443, не является redirect-запросом и закрывается TLS-слоем. Не подключайте порт 8443 как `http://`.

## Batch импорт

```bash
.venv/bin/vrphoto process /path/to/Photos /path/to/VRLibrary
.venv/bin/vrphoto process /path/to/photo.jpg /path/to/VRLibrary --force
```

Команда рекурсивно находит JPG/JPEG/PNG/WEBP, сохраняет вложенность и запускает **один** `sharp predict` для всех ожидающих файлов. Источники копируются во временный каталог с уникальными именами, поэтому одинаковые basenames в разных папках не конфликтуют в SHARP output. Preview создаётся через Pillow из исходной фотографии с учётом EXIF ориентации. Готовые сцены пропускаются по размеру и `mtime_ns`; `--force` обходит resume. При ошибке отдельной сцены остальные готовые файлы остаются. В конце выводятся `discovered`, `pending`, `processed`, `skipped`, `failed`, `total_seconds`.

Для импорта SHARP CLI должен принимать `sharp predict -i DIR -o DIR --device mps`. VRPhoto проверяет `sharp --help` и `sharp predict --help` в найденном окружении перед первым batch. Установленная у вас версия на Mac здесь не была доступна, поэтому этот шаг необходимо запустить на Mac.

## Настройки

Варианты через YAML (`vrphoto --config config.yaml serve ...`) или флаги CLI:

```yaml
sharp_path: /Users/artanis/WORK/GIT/sharp
preview_max_size: 600
preview_quality: 85
https_host: 0.0.0.0
https_port: 8443
http_redirect_port: null
state_dir: /Users/artanis/.local/share/vrphoto
certificate_file: null
private_key_file: null
ca_certificate_file: null
default_splat_budget: full
viewer_renderer_preference: auto
```

Флаги `serve`: `--host`, `--port`, `--state-dir`, `--http-redirect-port`, `--certificate-file`, `--private-key-file`, `--ca-certificate-file`, `--budget`, `--renderer`, `--debug`. HTTP redirect использует отдельный порт. При собственном сертификате передайте certificate и private key вместе. По умолчанию сервер слушает все интерфейсы локальной сети без аутентификации. `--debug` включает диагностические страницы, тестовый куб, отправку XR-журнала и старый PlayCanvas viewer; без флага эти маршруты и элементы интерфейса недоступны. Основной JS узнаёт режим через `/api/runtime`.

## PLY и камера

SHARP экспортирует OpenCV координаты: X вправо, Y вниз, Z вперёд. Viewer поворачивает splat на 180° вокруг X, а камера Three.js остаётся в исходном (0,0,0) и смотрит по −Z. Для VR используется `local-floor`, а первая отслеженная поза головы совмещается с исходной камерой фото. Desktop orbit/pan/zoom отключается на время XR; виртуальная locomotion не добавлена.

Если в PLY присутствуют SHARP `extrinsic` и `intrinsic`, они сохраняются в `metadata.json` как `sharp_camera`. По текущему исходному коду SHARP extrinsic — единичная матрица. Viewer не заменяет её произвольной orbit-позой.

Budget `?budget=0.6` или пресеты Low/Medium/High/Full сокращают исходный binary little-endian PLY на сервере с детерминированной выборкой splats и сохраняют результат в state cache. Выбранный пресет запоминается в браузере для следующих сцен; явный `?budget=` в URL имеет приоритет для открытой сцены. Это PoC-способ снизить нагрузку; пространственное LOD/streaming появится после перехода к SOG/LOD. Каталог и URL сцен остаются прежними. Ссылка **← Library** ведёт на ту же страницу каталога, с которой открыли сцену, включая номер страницы; при прямом открытии сцены ведёт в родительский каталог.

## Проверки

```bash
python -m pip install pytest
python -m pytest -q tests
```

Тесты покрывают доверенный HTTPS, preview, Range и большой PLY через `aiohttp.web.FileResponse`, Unicode, traversal, plain HTTP на TLS-порту, частичный handshake, concurrent clients, budget, batch/resume. На Windows сервер автоматически отключает `asyncio.sendfile` для `aiohttp`: при прерванной загрузке большого PLY он использует потоковую отправку частями, сохраняя поддержку Range. POSIX тест `SIGINT` выполняется на macOS/Linux; на Windows он пропускается из-за другой модели консольных сигналов.

Проверка реального PICO 4, установленного SHARP venv на Mac, фактического FPS на полном PLY и визуальной точности исходного ракурса остаётся устройственным этапом PoC. WebXR нельзя достоверно подтвердить в desktop-браузере без гарнитуры.

### Если PICO показывает рябь или чёрный экран

Основной Spark viewer использует WebGL2. `?renderer=webgpu` относится только к старому PlayCanvas viewer при `--debug`. Для следующих проверок запустите сервер с `--debug`; в обычном режиме диагностика скрыта. Перезапустите сервер после обновления проекта; адрес JS содержит номер ревизии, чтобы PICO не взял старую копию из кеша.

Если рябь начинается **уже в каталоге**, запустите сервер с `--debug`, откройте `/debug/plain`, затем `/debug/catalog-lite`. Первая страница не содержит стилей, изображений и JavaScript; вторая показывает названия сцен без preview. В обычном каталоге выводится максимум 48 preview-карточек на страницу, а сервер отдаёт маленькие кешируемые JPEG thumbnails. Это снижает декодирование изображений и нагрузку на PICO. Если рябь сохраняется даже на `/debug/plain`, проблема возникает до нашего рендеринга каталога.

До входа в VR страница сцены показывает статичный `preview.jpg`, а PLY загружает в фоне без отрисовки splats. Кнопка **Enter VR** становится доступна после загрузки PLY. После выхода статичный preview возвращается. В VR первая поза шлема выравнивается с камерой исходной фотографии. Рендер запускается по кадровому циклу WebXR; сглаживание splats делает Spark (`blurAmount=0.3`), а WebGL MSAA оставлен выключенным по рекомендации Spark из-за его стоимости.

В VR курок открывает панель **← Library** перед глазами. Наведите контроллер на панель и нажмите курок ещё раз для возврата в исходный каталог; нажатие мимо панели закрывает её. Удерживая боковую кнопку хвата (grip), можно перемещать всю сцену движением контроллера, в том числе вверх и вниз. Поворот кисти не наклоняет сцену. Если при этом двигать стик вперёд или назад, сцена приближается к зрителю или отдаляется вдоль направления взгляда. После отпускания сцена остаётся на новом месте. Используются стандартные WebXR `selectstart`, `squeezestart`, `squeezeend` и ось стика `xr-standard`.

При `--debug` события XR отправляются на сервер и пишутся в `xr-events.jsonl` внутри `state_dir`. Кнопка **Send XR log** на странице сцены и `/debug/capabilities` повторно отправляет последние 40 событий из браузера. Текущие события можно прочитать по `/debug/xr-events` с компьютера. Для проверки только Three.js/WebXR откройте сцену с `?xrtest=1`: PLY не загружается, в VR появляется красный куб, и сеанс завершается через 10 секунд. Для обычной сцены `?xrautoexit=10` также ограничит пробный VR сеанс.

Если даже красный куб чёрный, `/debug/xr-raw` проверяет WebXR без библиотек: WebGL2 напрямую заливает XR framebuffer сплошным красным цветом и завершает сеанс через 10 секунд. Его события тоже пишутся в серверный `xr-events.jsonl`.

Для прежнего PlayCanvas viewer используйте `?engine=playcanvas`. Его дополнительные тесты доступны по `?engine=playcanvas&xrtest=1` и `?engine=playcanvas&xrtest=1&xrfill=1`.

## Источники API

- [Apple SHARP CLI, PLY и координаты](https://github.com/apple-aiml-research/ml-sharp)
- [Spark: PLY viewer и WebXR example](https://github.com/sparkjsdev/spark/tree/main/examples/webxr)
- [Three.js WebXRManager](https://threejs.org/docs/#api/en/renderers/webxr/WebXRManager)
- [PlayCanvas Engine standalone](https://developer.playcanvas.com/user-manual/engine/standalone/)
- [PlayCanvas XrManager](https://api.playcanvas.com/engine/classes/XrManager.html)
- [PlayCanvas GSplatComponent](https://api.playcanvas.com/engine/classes/GSplatComponent.html)
- [PlayCanvas splat budget](https://api.playcanvas.com/engine/classes/GSplatParams.html)
- [MDN WebXR secure context](https://developer.mozilla.org/en-US/docs/Web/API/WebXR_Device_API/Startup_and_shutdown)
