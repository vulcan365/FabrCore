# App.razor — HTML Document Shell

The root component that serves as the HTML document shell. Defines CDN dependencies and load order.

```razor
<!DOCTYPE html>
<html lang="en">

<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <ResourcePreloader />

    <!-- Google Fonts: Figtree and Poppins -->
    <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Figtree:wght@400;500;600;700&family=Poppins:wght@500;600;700&display=swap" />

    <!-- Bootstrap 5 CSS -->
    <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet" integrity="sha384-QWTKZyjpPEjISv5WaRU9OFeRpok6YctnYmDr5pNlyT2bRjXh0JMhjY6hW+ALEwIH" crossorigin="anonymous">

    <!-- Bootstrap Icons -->
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css">

    <!-- App Design System -->
    <link rel="stylesheet" href="@Assets["css/design-tokens.css"]" />
    <link rel="stylesheet" href="@Assets["css/app.css"]" />
    <link rel="stylesheet" href="@Assets["css/layout.css"]" />
    <link rel="stylesheet" href="@Assets["css/utilities.css"]" />
    <link rel="stylesheet" href="@Assets["css/form-controls.css"]" />
    <link rel="stylesheet" href="@Assets["css/data-display.css"]" />

    <link rel="stylesheet" href="@Assets["YourApp.Web.styles.css"]" />
    <ImportMap />
    <link rel="icon" type="image/png" href="favicon.png" />
    <HeadOutlet />
</head>

<body>
    <AntiforgeryToken />
    <Routes />
    <ReconnectModal />

    <!-- Bootstrap 5 JS Bundle (Popper included) -->
    <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js" integrity="sha384-YvpcrYf0tY3lHB60NNkmXc5s9fDVZLESaAA55NDzOxhy9GkcIdslK1eN7N6jIeHz" crossorigin="anonymous"></script>
    <script src="@Assets["_framework/blazor.web.js"]"></script>
</body>

</html>
```
