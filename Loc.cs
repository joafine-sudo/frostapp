using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace FrostClient;

// Launcher translations. XAML binds to Loc.Instance (live language switch),
// code-behind uses Loc.T("key", args...).
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    // Code, name shown in the language picker (always in its own language).
    public static readonly (string Code, string Native)[] Languages =
    {
        ("en-GB", "English (United Kingdom)"),
        ("pt-PT", "Português (Portugal)"),
        ("es-ES", "Español (España)"),
        ("fr-FR", "Français (France)"),
        ("ru-RU", "Русский"),
        ("th-TH", "ไทย"),
    };

    public static string CurrentCode { get; private set; } = "en-GB";
    private static int _index = 0;

    public static event Action? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public static string DetectSystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "pt" => "pt-PT",
            "es" => "es-ES",
            "fr" => "fr-FR",
            "ru" => "ru-RU",
            "th" => "th-TH",
            _    => "en-GB",
        };

    public static void SetLanguage(string? code)
    {
        int i = Array.FindIndex(Languages, l => l.Code == code);
        if (i < 0) i = 0;
        if (Languages[i].Code == CurrentCode && i == _index) return;

        _index = i;
        CurrentCode = Languages[i].Code;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    // Used by XAML: {Binding [key], Source={x:Static loc:Loc.Instance}}
    public string this[string key] => Get(key);

    public static string Get(string key)
    {
        if (!Data.TryGetValue(key, out var v)) return key;
        var s = _index < v.Length ? v[_index] : v[0];
        return string.IsNullOrEmpty(s) ? v[0] : s;
    }

    public static string T(string key, params object?[] args)
    {
        var s = Get(key);
        if (args.Length == 0) return s;
        try { return string.Format(s, args); } catch { return s; }
    }

    // Order: en-GB, pt-PT, es-ES, fr-FR, ru-RU, th-TH
    private static readonly Dictionary<string, string[]> Data = new()
    {
        ["nav_start"] = new[] { "Start", "Início", "Inicio", "Accueil", "Главная", "หน้าแรก" },
        ["nav_versions"] = new[] { "Versions", "Versões", "Versiones", "Versions", "Версии", "เวอร์ชัน" },
        ["nav_settings"] = new[] { "Settings", "Definições", "Ajustes", "Paramètres", "Настройки", "การตั้งค่า" },
        ["nav_accounts"] = new[] { "Accounts", "Contas", "Cuentas", "Comptes", "Аккаунты", "บัญชี" },
        ["playing_as"] = new[] { "Playing as", "A jogar como", "Jugando como", "Joue en tant que", "Вы играете как", "เล่นในชื่อ" },
        ["not_logged_in"] = new[] { "Not logged in", "Sem sessão iniciada", "Sin sesión iniciada", "Non connecté", "Вход не выполнен", "ยังไม่ได้เข้าสู่ระบบ" },
        ["home_sub_default"] = new[] { "Ready when you are.", "Pronto quando quiseres.", "Listo cuando quieras.", "Prêt quand vous l'êtes.", "Готов, когда вы готовы.", "พร้อมเมื่อคุณพร้อม" },
        ["version"] = new[] { "VERSION", "VERSÃO", "VERSIÓN", "VERSION", "ВЕРСИЯ", "เวอร์ชัน" },
        ["rename_version_tip"] = new[] { "Rename this version", "Mudar o nome desta versão", "Cambiar el nombre de esta versión", "Renommer cette version", "Переименовать эту версию", "เปลี่ยนชื่อเวอร์ชันนี้" },
        ["play"] = new[] { "PLAY", "JOGAR", "JUGAR", "JOUER", "ИГРАТЬ", "เล่น" },
        ["add_account_tip"] = new[] { "Add account", "Adicionar conta", "Añadir cuenta", "Ajouter un compte", "Добавить аккаунт", "เพิ่มบัญชี" },
        ["manage_accounts_tip"] = new[] { "Manage accounts", "Gerir contas", "Gestionar cuentas", "Gérer les comptes", "Управление аккаунтами", "จัดการบัญชี" },
        ["open_minecraft"] = new[] { "OPEN .MINECRAFT", "ABRIR .MINECRAFT", "ABRIR .MINECRAFT", "OUVRIR .MINECRAFT", "ОТКРЫТЬ .MINECRAFT", "เปิด .MINECRAFT" },
        ["open_mods"] = new[] { "OPEN MODS FOLDER", "ABRIR PASTA DE MODS", "ABRIR CARPETA DE MODS", "OUVRIR LE DOSSIER MODS", "ОТКРЫТЬ ПАПКУ МОДОВ", "เปิดโฟลเดอร์ม็อด" },
        ["join_discord"] = new[] { "JOIN DISCORD", "ENTRAR NO DISCORD", "UNIRSE A DISCORD", "REJOINDRE DISCORD", "ВОЙТИ В DISCORD", "เข้าร่วม DISCORD" },
        ["join_discord_tip"] = new[] { "Join our Discord server", "Entra no nosso servidor de Discord", "Únete a nuestro servidor de Discord", "Rejoignez notre serveur Discord", "Присоединяйтесь к нашему серверу Discord", "เข้าร่วมเซิร์ฟเวอร์ Discord ของเรา" },
        ["versions_sub"] = new[] { "Install and manage your Minecraft versions.", "Instala e gere as tuas versões de Minecraft.", "Instala y gestiona tus versiones de Minecraft.", "Installez et gérez vos versions de Minecraft.", "Устанавливайте и управляйте версиями Minecraft.", "ติดตั้งและจัดการเวอร์ชัน Minecraft ของคุณ" },
        ["install_new"] = new[] { "INSTALL NEW VERSION", "INSTALAR NOVA VERSÃO", "INSTALAR NUEVA VERSIÓN", "INSTALLER UNE NOUVELLE VERSION", "УСТАНОВИТЬ НОВУЮ ВЕРСИЮ", "ติดตั้งเวอร์ชันใหม่" },
        ["mc_version"] = new[] { "Minecraft version", "Versão do Minecraft", "Versión de Minecraft", "Version de Minecraft", "Версия Minecraft", "เวอร์ชัน Minecraft" },
        ["download_install"] = new[] { "DOWNLOAD AND INSTALL", "DESCARREGAR E INSTALAR", "DESCARGAR E INSTALAR", "TÉLÉCHARGER ET INSTALLER", "СКАЧАТЬ И УСТАНОВИТЬ", "ดาวน์โหลดและติดตั้ง" },
        ["waiting_install"] = new[] { "Waiting for installation…", "A aguardar a instalação…", "Esperando la instalación…", "En attente de l'installation…", "Ожидание установки…", "กำลังรอการติดตั้ง…" },
        ["installed_versions"] = new[] { "INSTALLED VERSIONS", "VERSÕES INSTALADAS", "VERSIONES INSTALADAS", "VERSIONS INSTALLÉES", "УСТАНОВЛЕННЫЕ ВЕРСИИ", "เวอร์ชันที่ติดตั้งแล้ว" },
        ["refresh"] = new[] { "REFRESH", "ATUALIZAR", "ACTUALIZAR", "ACTUALISER", "ОБНОВИТЬ", "รีเฟรช" },
        ["select"] = new[] { "SELECT", "SELECIONAR", "SELECCIONAR", "SÉLECTIONNER", "ВЫБРАТЬ", "เลือก" },
        ["rename"] = new[] { "Rename", "Mudar nome", "Renombrar", "Renommer", "Переименовать", "เปลี่ยนชื่อ" },
        ["delete"] = new[] { "Delete", "Eliminar", "Eliminar", "Supprimer", "Удалить", "ลบ" },
        ["settings_sub"] = new[] { "Customize your Frost Client experience.", "Personaliza a tua experiência no Frost Client.", "Personaliza tu experiencia en Frost Client.", "Personnalisez votre expérience Frost Client.", "Настройте Frost Client под себя.", "ปรับแต่งประสบการณ์ Frost Client ของคุณ" },
        ["sec_game"] = new[] { "Game", "Jogo", "Juego", "Jeu", "Игра", "เกม" },
        ["ram"] = new[] { "Allocated RAM", "RAM alocada", "RAM asignada", "RAM allouée", "Выделенная ОЗУ", "RAM ที่จัดสรร" },
        ["ram_desc"] = new[] { "Memory dedicated to Minecraft", "Memória dedicada ao Minecraft", "Memoria dedicada a Minecraft", "Mémoire dédiée à Minecraft", "Память, выделенная для Minecraft", "หน่วยความจำที่ใช้สำหรับ Minecraft" },
        ["ram_rec"] = new[] { "Recommended: 4 GB", "Recomendado: 4 GB", "Recomendado: 4 GB", "Recommandé : 4 Go", "Рекомендуется: 4 ГБ", "แนะนำ: 4 GB" },
        ["res"] = new[] { "Game Resolution", "Resolução do jogo", "Resolución del juego", "Résolution du jeu", "Разрешение игры", "ความละเอียดของเกม" },
        ["res_desc"] = new[] { "Window size when Minecraft starts", "Tamanho da janela quando o Minecraft inicia", "Tamaño de la ventana al iniciar Minecraft", "Taille de la fenêtre au démarrage de Minecraft", "Размер окна при запуске Minecraft", "ขนาดหน้าต่างเมื่อเริ่ม Minecraft" },
        ["res_default"] = new[] { "Default", "Predefinição", "Predeterminado", "Par défaut", "По умолчанию", "ค่าเริ่มต้น" },
        ["on_open"] = new[] { "When the game opens", "Quando o jogo abre", "Cuando se abre el juego", "À l'ouverture du jeu", "При запуске игры", "เมื่อเกมเปิด" },
        ["on_open_desc"] = new[] { "What happens to the launcher when Minecraft starts", "O que acontece ao launcher quando o Minecraft inicia", "Qué pasa con el launcher cuando se inicia Minecraft", "Ce qui arrive au launcher au démarrage de Minecraft", "Что происходит с лаунчером при запуске Minecraft", "สิ่งที่เกิดขึ้นกับตัวเปิดเกมเมื่อ Minecraft เริ่มทำงาน" },
        ["keep_open"] = new[] { "Keep launcher open", "Manter o launcher aberto", "Mantener el launcher abierto", "Garder le launcher ouvert", "Оставить лаунчер открытым", "เปิดตัวเปิดเกมไว้" },
        ["minimize"] = new[] { "Minimize to taskbar", "Minimizar para a barra de tarefas", "Minimizar a la barra de tareas", "Réduire dans la barre des tâches", "Свернуть на панель задач", "ย่อไปที่แถบงาน" },
        ["discord_rpc_desc"] = new[] { "Show your current game on Discord", "Mostrar o teu jogo atual no Discord", "Mostrar tu juego actual en Discord", "Afficher votre jeu actuel sur Discord", "Показывать вашу текущую игру в Discord", "แสดงเกมที่คุณกำลังเล่นบน Discord" },
        ["sec_appearance"] = new[] { "Appearance", "Aparência", "Apariencia", "Apparence", "Оформление", "รูปลักษณ์" },
        ["bg"] = new[] { "Background", "Fundo", "Fondo", "Arrière-plan", "Фон", "พื้นหลัง" },
        ["bg_desc"] = new[] { "Wallpaper shown behind the launcher", "Imagem de fundo mostrada atrás do launcher", "Imagen de fondo detrás del launcher", "Image affichée derrière le launcher", "Изображение на фоне лаунчера", "ภาพพื้นหลังที่แสดงหลังตัวเปิดเกม" },
        ["wp_default"] = new[] { "Default (Snow)", "Predefinido (Neve)", "Predeterminado (Nieve)", "Par défaut (Neige)", "По умолчанию (Снег)", "ค่าเริ่มต้น (หิมะ)" },
        ["wp_iceland"] = new[] { "Iceland", "Islândia", "Islandia", "Islande", "Исландия", "ไอซ์แลนด์" },
        ["wp_house"] = new[] { "House", "Casa", "Casa", "Maison", "Дом", "บ้าน" },
        ["wp_fox"] = new[] { "Fox", "Raposa", "Zorro", "Renard", "Лиса", "สุนัขจิ้งจอก" },
        ["language"] = new[] { "Language", "Idioma", "Idioma", "Langue", "Язык", "ภาษา" },
        ["language_desc"] = new[] { "Choose the launcher language", "Escolhe o idioma do launcher", "Elige el idioma del launcher", "Choisissez la langue du launcher", "Выберите язык лаунчера", "เลือกภาษาของตัวเปิดเกม" },
        ["java_exe"] = new[] { "Java Executable", "Executável do Java", "Ejecutable de Java", "Exécutable Java", "Исполняемый файл Java", "ไฟล์ปฏิบัติการ Java" },
        ["java_exe_desc"] = new[] { "Leave empty for automatic detection", "Deixa vazio para deteção automática", "Déjalo vacío para detección automática", "Laissez vide pour une détection automatique", "Оставьте пустым для автоопределения", "เว้นว่างไว้เพื่อตรวจหาอัตโนมัติ" },
        ["browse"] = new[] { "BROWSE", "PROCURAR", "EXAMINAR", "PARCOURIR", "ОБЗОР", "เรียกดู" },
        ["jvm_args"] = new[] { "Custom JVM Arguments", "Argumentos JVM personalizados", "Argumentos JVM personalizados", "Arguments JVM personnalisés", "Пользовательские аргументы JVM", "อาร์กิวเมนต์ JVM ที่กำหนดเอง" },
        ["jvm_args_desc"] = new[] { "Passed directly to the JVM on launch", "Passados diretamente à JVM ao iniciar", "Se pasan directamente a la JVM al iniciar", "Transmis directement à la JVM au lancement", "Передаются напрямую в JVM при запуске", "ส่งตรงไปยัง JVM เมื่อเริ่มเกม" },
        ["preset_balanced"] = new[] { "Balanced", "Equilibrado", "Equilibrado", "Équilibré", "Сбалансированный", "สมดุล" },
        ["preset_perf"] = new[] { "Performance", "Desempenho", "Rendimiento", "Performance", "Производительность", "ประสิทธิภาพ" },
        ["preset_maxfps"] = new[] { "Max FPS", "FPS máximo", "FPS máximo", "FPS max", "Макс. FPS", "FPS สูงสุด" },
        ["preset_clear"] = new[] { "Clear", "Limpar", "Borrar", "Effacer", "Очистить", "ล้าง" },
        ["save_settings"] = new[] { "SAVE SETTINGS", "GUARDAR DEFINIÇÕES", "GUARDAR AJUSTES", "ENREGISTRER LES PARAMÈTRES", "СОХРАНИТЬ НАСТРОЙКИ", "บันทึกการตั้งค่า" },
        ["accounts_sub"] = new[] { "Manage your Offline and Microsoft accounts.", "Gere as tuas contas Offline e Microsoft.", "Gestiona tus cuentas sin conexión y de Microsoft.", "Gérez vos comptes hors ligne et Microsoft.", "Управляйте офлайн- и Microsoft-аккаунтами.", "จัดการบัญชีออฟไลน์และบัญชี Microsoft ของคุณ" },
        ["active_account"] = new[] { "ACTIVE ACCOUNT", "CONTA ATIVA", "CUENTA ACTIVA", "COMPTE ACTIF", "АКТИВНЫЙ АККАУНТ", "บัญชีที่ใช้งานอยู่" },
        ["add_offline_title"] = new[] { "ADD OFFLINE ACCOUNT", "ADICIONAR CONTA OFFLINE", "AÑADIR CUENTA SIN CONEXIÓN", "AJOUTER UN COMPTE HORS LIGNE", "ДОБАВИТЬ ОФЛАЙН-АККАУНТ", "เพิ่มบัญชีออฟไลน์" },
        ["add_offline_desc"] = new[] { "Create a local account using a username.", "Cria uma conta local com um nome de utilizador.", "Crea una cuenta local con un nombre de usuario.", "Créez un compte local avec un nom d'utilisateur.", "Создайте локальный аккаунт по имени пользователя.", "สร้างบัญชีในเครื่องด้วยชื่อผู้ใช้" },
        ["add_offline_btn"] = new[] { "ADD OFFLINE", "ADICIONAR OFFLINE", "AÑADIR SIN CONEXIÓN", "AJOUTER HORS LIGNE", "ДОБАВИТЬ ОФЛАЙН", "เพิ่มออฟไลน์" },
        ["ms_account"] = new[] { "MICROSOFT ACCOUNT", "CONTA MICROSOFT", "CUENTA DE MICROSOFT", "COMPTE MICROSOFT", "АККАУНТ MICROSOFT", "บัญชี MICROSOFT" },
        ["ms_desc"] = new[] { "Sign in with your Microsoft account to play Minecraft.", "Inicia sessão com a tua conta Microsoft para jogar Minecraft.", "Inicia sesión con tu cuenta de Microsoft para jugar a Minecraft.", "Connectez-vous avec votre compte Microsoft pour jouer à Minecraft.", "Войдите через аккаунт Microsoft, чтобы играть в Minecraft.", "เข้าสู่ระบบด้วยบัญชี Microsoft เพื่อเล่น Minecraft" },
        ["ms_signin"] = new[] { "SIGN IN WITH MICROSOFT", "INICIAR SESSÃO COM MICROSOFT", "INICIAR SESIÓN CON MICROSOFT", "SE CONNECTER AVEC MICROSOFT", "ВОЙТИ ЧЕРЕЗ MICROSOFT", "เข้าสู่ระบบด้วย MICROSOFT" },
        ["saved_accounts"] = new[] { "SAVED ACCOUNTS", "CONTAS GUARDADAS", "CUENTAS GUARDADAS", "COMPTES ENREGISTRÉS", "СОХРАНЁННЫЕ АККАУНТЫ", "บัญชีที่บันทึกไว้" },
        ["remove_account_tip"] = new[] { "Remove account", "Remover conta", "Quitar cuenta", "Supprimer le compte", "Удалить аккаунт", "ลบบัญชี" },
        ["status_ready"] = new[] { "Ready.", "Pronto.", "Listo.", "Prêt.", "Готово.", "พร้อมแล้ว" },
        ["discord_connecting"] = new[] { "Discord: connecting…", "Discord: a ligar…", "Discord: conectando…", "Discord : connexion…", "Discord: подключение…", "Discord: กำลังเชื่อมต่อ…" },
        ["website"] = new[] { "Website", "Site", "Sitio web", "Site web", "Сайт", "เว็บไซต์" },
        ["wiz_welcome"] = new[] { "Welcome to Frost Client", "Bem-vindo ao Frost Client", "Te damos la bienvenida a Frost Client", "Bienvenue sur Frost Client", "Добро пожаловать в Frost Client", "ยินดีต้อนรับสู่ Frost Client" },
        ["wiz_lets"] = new[] { "Let's get your launcher ready.", "Vamos preparar o teu launcher.", "Vamos a preparar tu launcher.", "Préparons votre launcher.", "Давайте подготовим ваш лаунчер.", "มาเตรียมตัวเปิดเกมของคุณกัน" },
        ["quick_setup"] = new[] { "QUICK SETUP", "CONFIGURAÇÃO RÁPIDA", "CONFIGURACIÓN RÁPIDA", "CONFIGURATION RAPIDE", "БЫСТРАЯ НАСТРОЙКА", "ตั้งค่าอย่างรวดเร็ว" },
        ["wiz_1"] = new[] { "1. Configure your Java installation.", "1. Configura a tua instalação do Java.", "1. Configura tu instalación de Java.", "1. Configurez votre installation de Java.", "1. Настройте установку Java.", "1. ตั้งค่า Java ของคุณ" },
        ["wiz_2"] = new[] { "2. Choose your account.", "2. Escolhe a tua conta.", "2. Elige tu cuenta.", "2. Choisissez votre compte.", "2. Выберите аккаунт.", "2. เลือกบัญชีของคุณ" },
        ["wiz_3"] = new[] { "3. Start playing Minecraft.", "3. Começa a jogar Minecraft.", "3. Empieza a jugar a Minecraft.", "3. Commencez à jouer à Minecraft.", "3. Начните играть в Minecraft.", "3. เริ่มเล่น Minecraft" },
        ["get_started"] = new[] { "GET STARTED", "COMEÇAR", "EMPEZAR", "COMMENCER", "НАЧАТЬ", "เริ่มต้นใช้งาน" },
        ["java_config"] = new[] { "Java Configuration", "Configuração do Java", "Configuración de Java", "Configuration de Java", "Настройка Java", "การตั้งค่า Java" },
        ["java_config_desc"] = new[] { "Select the Java executable used to launch Minecraft.", "Seleciona o executável do Java usado para iniciar o Minecraft.", "Selecciona el ejecutable de Java que se usa para iniciar Minecraft.", "Sélectionnez l'exécutable Java utilisé pour lancer Minecraft.", "Выберите исполняемый файл Java для запуска Minecraft.", "เลือกไฟล์ปฏิบัติการ Java ที่ใช้เปิด Minecraft" },
        ["java_exe_caps"] = new[] { "JAVA EXECUTABLE", "EXECUTÁVEL DO JAVA", "EJECUTABLE DE JAVA", "EXÉCUTABLE JAVA", "ИСПОЛНЯЕМЫЙ ФАЙЛ JAVA", "ไฟล์ปฏิบัติการ JAVA" },
        ["java_auto"] = new[] { "Leave empty for automatic Java detection.", "Deixa vazio para deteção automática do Java.", "Déjalo vacío para detectar Java automáticamente.", "Laissez vide pour détecter Java automatiquement.", "Оставьте пустым для автоопределения Java.", "เว้นว่างไว้เพื่อตรวจหา Java อัตโนมัติ" },
        ["back"] = new[] { "BACK", "VOLTAR", "ATRÁS", "RETOUR", "НАЗАД", "ย้อนกลับ" },
        ["continue"] = new[] { "CONTINUE", "CONTINUAR", "CONTINUAR", "CONTINUER", "ПРОДОЛЖИТЬ", "ต่อไป" },
        ["choose_account"] = new[] { "Choose Your Account", "Escolhe a tua conta", "Elige tu cuenta", "Choisissez votre compte", "Выберите аккаунт", "เลือกบัญชีของคุณ" },
        ["choose_account_desc"] = new[] { "How do you want to authenticate with Frost Client?", "Como queres autenticar-te no Frost Client?", "¿Cómo quieres autenticarte en Frost Client?", "Comment souhaitez-vous vous authentifier sur Frost Client ?", "Как вы хотите войти в Frost Client?", "คุณต้องการยืนยันตัวตนกับ Frost Client อย่างไร" },
        ["ms_official"] = new[] { "Sign in with your official Minecraft account.", "Inicia sessão com a tua conta oficial do Minecraft.", "Inicia sesión con tu cuenta oficial de Minecraft.", "Connectez-vous avec votre compte Minecraft officiel.", "Войдите с официальным аккаунтом Minecraft.", "เข้าสู่ระบบด้วยบัญชี Minecraft อย่างเป็นทางการ" },
        ["login_ms"] = new[] { "LOGIN WITH MICROSOFT", "ENTRAR COM MICROSOFT", "ENTRAR CON MICROSOFT", "SE CONNECTER AVEC MICROSOFT", "ВОЙТИ ЧЕРЕЗ MICROSOFT", "เข้าสู่ระบบด้วย MICROSOFT" },
        ["offline_account"] = new[] { "OFFLINE ACCOUNT", "CONTA OFFLINE", "CUENTA SIN CONEXIÓN", "COMPTE HORS LIGNE", "ОФЛАЙН-АККАУНТ", "บัญชีออฟไลน์" },
        ["offline_desc"] = new[] { "Enter a local username (no Microsoft needed).", "Introduz um nome de utilizador local (sem Microsoft).", "Introduce un nombre de usuario local (sin Microsoft).", "Saisissez un nom d'utilisateur local (sans Microsoft).", "Введите локальное имя (Microsoft не нужен).", "ใส่ชื่อผู้ใช้ในเครื่อง (ไม่ต้องใช้ Microsoft)" },
        ["continue_offline"] = new[] { "CONTINUE OFFLINE", "CONTINUAR OFFLINE", "CONTINUAR SIN CONEXIÓN", "CONTINUER HORS LIGNE", "ПРОДОЛЖИТЬ ОФЛАЙН", "ดำเนินการต่อแบบออฟไลน์" },
        ["confirm_action"] = new[] { "Confirm action", "Confirmar ação", "Confirmar acción", "Confirmer l'action", "Подтвердите действие", "ยืนยันการดำเนินการ" },
        ["are_you_sure"] = new[] { "Are you sure?", "Tens a certeza?", "¿Estás seguro?", "Êtes-vous sûr ?", "Вы уверены?", "คุณแน่ใจหรือไม่?" },
        ["cancel"] = new[] { "CANCEL", "CANCELAR", "CANCELAR", "ANNULER", "ОТМЕНА", "ยกเลิก" },
        ["delete_btn"] = new[] { "DELETE", "ELIMINAR", "ELIMINAR", "SUPPRIMER", "УДАЛИТЬ", "ลบ" },
        ["rename_version"] = new[] { "Rename Version", "Mudar nome da versão", "Renombrar versión", "Renommer la version", "Переименовать версию", "เปลี่ยนชื่อเวอร์ชัน" },
        ["rename_desc"] = new[] { "Enter a display name for this version.", "Introduz um nome de apresentação para esta versão.", "Introduce un nombre para mostrar de esta versión.", "Saisissez un nom d'affichage pour cette version.", "Введите отображаемое имя для этой версии.", "ใส่ชื่อที่แสดงสำหรับเวอร์ชันนี้" },
        ["display_name"] = new[] { "DISPLAY NAME", "NOME DE APRESENTAÇÃO", "NOMBRE PARA MOSTRAR", "NOM D'AFFICHAGE", "ОТОБРАЖАЕМОЕ ИМЯ", "ชื่อที่แสดง" },
        ["save"] = new[] { "SAVE", "GUARDAR", "GUARDAR", "ENREGISTRER", "СОХРАНИТЬ", "บันทึก" },
        ["resources_title"] = new[] { "Frost Client Resources", "Recursos do Frost Client", "Recursos de Frost Client", "Ressources de Frost Client", "Ресурсы Frost Client", "ทรัพยากรของ Frost Client" },
        ["resources_checking"] = new[] { "Checking Frost Client files…", "A verificar os ficheiros do Frost Client…", "Comprobando los archivos de Frost Client…", "Vérification des fichiers de Frost Client…", "Проверка файлов Frost Client…", "กำลังตรวจสอบไฟล์ Frost Client…" },
        ["skip"] = new[] { "SKIP", "IGNORAR", "OMITIR", "IGNORER", "ПРОПУСТИТЬ", "ข้าม" },
        ["retry"] = new[] { "RETRY", "TENTAR NOVAMENTE", "REINTENTAR", "RÉESSAYER", "ПОВТОРИТЬ", "ลองอีกครั้ง" },
        ["type_offline"] = new[] { "Offline", "Offline", "Sin conexión", "Hors ligne", "Офлайн", "ออฟไลน์" },
        ["discord_user"] = new[] { "Discord: {0}", "Discord: {0}", "Discord: {0}", "Discord : {0}", "Discord: {0}", "Discord: {0}" },
        ["discord_not_running"] = new[] { "Discord: not running", "Discord: não está a correr", "Discord: no se está ejecutando", "Discord : non lancé", "Discord: не запущен", "Discord: ไม่ได้เปิดอยู่" },
        ["discord_error"] = new[] { "Discord error: {0}", "Erro do Discord: {0}", "Error de Discord: {0}", "Erreur Discord : {0}", "Ошибка Discord: {0}", "ข้อผิดพลาด Discord: {0}" },
        ["select_javaw"] = new[] { "Select javaw.exe", "Seleciona o javaw.exe", "Selecciona javaw.exe", "Sélectionnez javaw.exe", "Выберите javaw.exe", "เลือก javaw.exe" },
        ["err_java_missing"] = new[] { "The file does not exist. Pick a valid javaw.exe or leave it empty.", "O ficheiro não existe. Escolhe um javaw.exe válido ou deixa vazio.", "El archivo no existe. Elige un javaw.exe válido o déjalo vacío.", "Le fichier n'existe pas. Choisissez un javaw.exe valide ou laissez vide.", "Файл не существует. Выберите корректный javaw.exe или оставьте поле пустым.", "ไม่พบไฟล์ โปรดเลือก javaw.exe ที่ถูกต้องหรือเว้นว่างไว้" },
        ["title_invalid_java"] = new[] { "Invalid Java", "Java inválido", "Java no válido", "Java invalide", "Неверный Java", "Java ไม่ถูกต้อง" },
        ["err_name"] = new[] { "Name must be 3–16 characters (letters, numbers or _).", "O nome deve ter 3–16 caracteres (letras, números ou _).", "El nombre debe tener 3–16 caracteres (letras, números o _).", "Le nom doit comporter 3–16 caractères (lettres, chiffres ou _).", "Имя должно содержать 3–16 символов (буквы, цифры или _).", "ชื่อต้องมี 3–16 ตัวอักษร (ตัวอักษร ตัวเลข หรือ _)" },
        ["title_invalid_name"] = new[] { "Invalid Name", "Nome inválido", "Nombre no válido", "Nom invalide", "Недопустимое имя", "ชื่อไม่ถูกต้อง" },
        ["status_settings_saved"] = new[] { "Settings saved.", "Definições guardadas.", "Ajustes guardados.", "Paramètres enregistrés.", "Настройки сохранены.", "บันทึกการตั้งค่าแล้ว" },
        ["box_settings_saved"] = new[] { "Settings saved!", "Definições guardadas!", "¡Ajustes guardados!", "Paramètres enregistrés !", "Настройки сохранены!", "บันทึกการตั้งค่าแล้ว!" },
        ["restoring_ms"] = new[] { "Restoring Microsoft session…", "A restaurar a sessão Microsoft…", "Restaurando la sesión de Microsoft…", "Restauration de la session Microsoft…", "Восстановление сессии Microsoft…", "กำลังกู้คืนเซสชัน Microsoft…" },
        ["ms_expired"] = new[] { "Microsoft session expired. Please log in again.", "A sessão Microsoft expirou. Inicia sessão novamente.", "La sesión de Microsoft expiró. Inicia sesión de nuevo.", "La session Microsoft a expiré. Reconnectez-vous.", "Сессия Microsoft истекла. Войдите снова.", "เซสชัน Microsoft หมดอายุ โปรดเข้าสู่ระบบอีกครั้ง" },
        ["offline_added"] = new[] { "Offline account '{0}' added.", "Conta offline '{0}' adicionada.", "Cuenta sin conexión '{0}' añadida.", "Compte hors ligne '{0}' ajouté.", "Офлайн-аккаунт '{0}' добавлен.", "เพิ่มบัญชีออฟไลน์ '{0}' แล้ว" },
        ["opening_ms"] = new[] { "Opening Microsoft login…", "A abrir o login da Microsoft…", "Abriendo el inicio de sesión de Microsoft…", "Ouverture de la connexion Microsoft…", "Открываем вход Microsoft…", "กำลังเปิดหน้าเข้าสู่ระบบ Microsoft…" },
        ["logged_in_as"] = new[] { "Logged in as {0}.", "Sessão iniciada como {0}.", "Sesión iniciada como {0}.", "Connecté en tant que {0}.", "Вы вошли как {0}.", "เข้าสู่ระบบในชื่อ {0} แล้ว" },
        ["login_failed"] = new[] { "Login failed.", "Falha no login.", "Error al iniciar sesión.", "Échec de la connexion.", "Не удалось войти.", "เข้าสู่ระบบไม่สำเร็จ" },
        ["title_login_error"] = new[] { "Login Error", "Erro de login", "Error de inicio de sesión", "Erreur de connexion", "Ошибка входа", "ข้อผิดพลาดในการเข้าสู่ระบบ" },
        ["active_account_status"] = new[] { "Active account: {0}.", "Conta ativa: {0}.", "Cuenta activa: {0}.", "Compte actif : {0}.", "Активный аккаунт: {0}.", "บัญชีที่ใช้งาน: {0}" },
        ["delete_account_q"] = new[] { "Delete account?", "Eliminar conta?", "¿Eliminar cuenta?", "Supprimer le compte ?", "Удалить аккаунт?", "ลบบัญชีหรือไม่?" },
        ["delete_account_msg"] = new[] { "Remove '{0}' from this launcher? This does not delete the online account itself.", "Remover '{0}' deste launcher? Isto não apaga a conta online em si.", "¿Quitar '{0}' de este launcher? Esto no elimina la cuenta en línea.", "Supprimer '{0}' de ce launcher ? Cela ne supprime pas le compte en ligne lui-même.", "Удалить '{0}' из лаунчера? Сам онлайн-аккаунт удалён не будет.", "ลบ '{0}' ออกจากตัวเปิดเกมนี้หรือไม่ การดำเนินการนี้ไม่ลบบัญชีออนไลน์จริง" },
        ["no_version"] = new[] { "NO VERSION", "SEM VERSÃO", "SIN VERSIÓN", "AUCUNE VERSION", "НЕТ ВЕРСИИ", "ไม่มีเวอร์ชัน" },
        ["install_to_start"] = new[] { "Install a version to get started.", "Instala uma versão para começar.", "Instala una versión para empezar.", "Installez une version pour commencer.", "Установите версию, чтобы начать.", "ติดตั้งเวอร์ชันเพื่อเริ่มต้น" },
        ["ready_to_launch"] = new[] { "{0}  ·  Ready to launch.", "{0}  ·  Pronto para iniciar.", "{0}  ·  Listo para iniciar.", "{0}  ·  Prêt à lancer.", "{0}  ·  Готово к запуску.", "{0}  ·  พร้อมเปิดเกม" },
        ["download_failed"] = new[] { "Download failed: {0}\n\nCheck your internet connection and try again.", "Falha na transferência: {0}\n\nVerifica a tua ligação à internet e tenta novamente.", "Error de descarga: {0}\n\nComprueba tu conexión a internet e inténtalo de nuevo.", "Échec du téléchargement : {0}\n\nVérifiez votre connexion internet et réessayez.", "Ошибка загрузки: {0}\n\nПроверьте подключение к интернету и повторите попытку.", "ดาวน์โหลดไม่สำเร็จ: {0}\n\nตรวจสอบการเชื่อมต่ออินเทอร์เน็ตแล้วลองอีกครั้ง" },
        ["select_mc_first"] = new[] { "Select a Minecraft version first.", "Seleciona primeiro uma versão do Minecraft.", "Selecciona primero una versión de Minecraft.", "Sélectionnez d'abord une version de Minecraft.", "Сначала выберите версию Minecraft.", "โปรดเลือกเวอร์ชัน Minecraft ก่อน" },
        ["downloading_mc"] = new[] { "Downloading Minecraft {0}…", "A descarregar o Minecraft {0}…", "Descargando Minecraft {0}…", "Téléchargement de Minecraft {0}…", "Загрузка Minecraft {0}…", "กำลังดาวน์โหลด Minecraft {0}…" },
        ["installing_mc"] = new[] { "Installing {0}…", "A instalar {0}…", "Instalando {0}…", "Installation de {0}…", "Установка {0}…", "กำลังติดตั้ง {0}…" },
        ["installed_ok"] = new[] { "{0} installed successfully!", "{0} instalado com sucesso!", "¡{0} instalado correctamente!", "{0} installé avec succès !", "{0} успешно установлен!", "ติดตั้ง {0} สำเร็จ!" },
        ["installed_status"] = new[] { "{0} installed.", "{0} instalado.", "{0} instalado.", "{0} installé.", "{0} установлен.", "ติดตั้ง {0} แล้ว" },
        ["install_error"] = new[] { "Installation error.", "Erro de instalação.", "Error de instalación.", "Erreur d'installation.", "Ошибка установки.", "การติดตั้งผิดพลาด" },
        ["failed"] = new[] { "Failed.", "Falhou.", "Falló.", "Échec.", "Сбой.", "ล้มเหลว" },
        ["title_install_error"] = new[] { "Install Error", "Erro de instalação", "Error de instalación", "Erreur d'installation", "Ошибка установки", "ข้อผิดพลาดในการติดตั้ง" },
        ["selected_version"] = new[] { "Selected {0}.", "{0} selecionado.", "{0} seleccionado.", "{0} sélectionné.", "Выбрано: {0}.", "เลือก {0} แล้ว" },
        ["delete_version_q"] = new[] { "Delete this version?", "Eliminar esta versão?", "¿Eliminar esta versión?", "Supprimer cette version ?", "Удалить эту версию?", "ลบเวอร์ชันนี้หรือไม่?" },
        ["delete_version_msg"] = new[] { "Delete '{0}'? Version files will be removed. Your worlds and mods will be kept.", "Eliminar '{0}'? Os ficheiros da versão serão removidos. Os teus mundos e mods serão mantidos.", "¿Eliminar '{0}'? Se quitarán los archivos de la versión. Tus mundos y mods se conservarán.", "Supprimer '{0}' ? Les fichiers de la version seront supprimés. Vos mondes et mods seront conservés.", "Удалить '{0}'? Файлы версии будут удалены. Ваши миры и моды сохранятся.", "ลบ '{0}' หรือไม่ ไฟล์เวอร์ชันจะถูกลบ แต่โลกและม็อดของคุณจะยังอยู่" },
        ["deleted_version"] = new[] { "Deleted {0}.", "{0} eliminado.", "{0} eliminado.", "{0} supprimé.", "{0} удалён.", "ลบ {0} แล้ว" },
        ["title_delete_error"] = new[] { "Delete Error", "Erro ao eliminar", "Error al eliminar", "Erreur de suppression", "Ошибка удаления", "ข้อผิดพลาดในการลบ" },
        ["real_id"] = new[] { "Real id: {0}", "ID real: {0}", "ID real: {0}", "ID réel : {0}", "Реальный ID: {0}", "ID จริง: {0}" },
        ["no_account_selected"] = new[] { "No account selected. Add one in the Account tab.", "Nenhuma conta selecionada. Adiciona uma no separador Contas.", "Ninguna cuenta seleccionada. Añade una en la pestaña Cuentas.", "Aucun compte sélectionné. Ajoutez-en un dans l'onglet Comptes.", "Аккаунт не выбран. Добавьте его на вкладке «Аккаунты».", "ยังไม่ได้เลือกบัญชี เพิ่มบัญชีได้ในแท็บบัญชี" },
        ["restore_account_failed"] = new[] { "Could not restore the selected account. Try logging in again.", "Não foi possível restaurar a conta selecionada. Tenta iniciar sessão novamente.", "No se pudo restaurar la cuenta seleccionada. Intenta iniciar sesión de nuevo.", "Impossible de restaurer le compte sélectionné. Essayez de vous reconnecter.", "Не удалось восстановить выбранный аккаунт. Попробуйте войти снова.", "ไม่สามารถกู้คืนบัญชีที่เลือก ลองเข้าสู่ระบบอีกครั้ง" },
        ["no_version_selected"] = new[] { "No version selected. Go to Versions.", "Nenhuma versão selecionada. Vai a Versões.", "Ninguna versión seleccionada. Ve a Versiones.", "Aucune version sélectionnée. Allez dans Versions.", "Версия не выбрана. Перейдите в «Версии».", "ยังไม่ได้เลือกเวอร์ชัน ไปที่เวอร์ชัน" },
        ["preparing"] = new[] { "Preparing {0}…", "A preparar {0}…", "Preparando {0}…", "Préparation de {0}…", "Подготовка {0}…", "กำลังเตรียม {0}…" },
        ["checking_resources"] = new[] { "Checking Frost Client resources…", "A verificar os recursos do Frost Client…", "Comprobando los recursos de Frost Client…", "Vérification des ressources de Frost Client…", "Проверка ресурсов Frost Client…", "กำลังตรวจสอบทรัพยากร Frost Client…" },
        ["resources_missing_box"] = new[] { "Frost Client resources are not installed.\n\nStatus: {0}\n\nCheck your internet connection and press PLAY again.", "Os recursos do Frost Client não estão instalados.\n\nEstado: {0}\n\nVerifica a tua ligação à internet e carrega em JOGAR novamente.", "Los recursos de Frost Client no están instalados.\n\nEstado: {0}\n\nComprueba tu conexión a internet y pulsa JUGAR de nuevo.", "Les ressources de Frost Client ne sont pas installées.\n\nÉtat : {0}\n\nVérifiez votre connexion internet et appuyez de nouveau sur JOUER.", "Ресурсы Frost Client не установлены.\n\nСостояние: {0}\n\nПроверьте подключение к интернету и снова нажмите «ИГРАТЬ».", "ยังไม่ได้ติดตั้งทรัพยากรของ Frost Client\n\nสถานะ: {0}\n\nตรวจสอบการเชื่อมต่ออินเทอร์เน็ตแล้วกดเล่นอีกครั้ง" },
        ["missing_resources"] = new[] { "Missing resources.", "Recursos em falta.", "Faltan recursos.", "Ressources manquantes.", "Ресурсы отсутствуют.", "ไม่พบทรัพยากร" },
        ["verifying"] = new[] { "Verifying files…", "A verificar ficheiros…", "Verificando archivos…", "Vérification des fichiers…", "Проверка файлов…", "กำลังตรวจสอบไฟล์…" },
        ["checking_runtime"] = new[] { "Checking runtime…", "A verificar o runtime…", "Comprobando el runtime…", "Vérification du runtime…", "Проверка среды выполнения…", "กำลังตรวจสอบรันไทม์…" },
        ["game_closed"] = new[] { "Game closed.", "Jogo fechado.", "Juego cerrado.", "Jeu fermé.", "Игра закрыта.", "ปิดเกมแล้ว" },
        ["game_crash_box"] = new[] { "The game closed with an error (code {0}).\n\nA log was saved at:\n{1}\n\nSend this file to the developer.", "O jogo fechou com um erro (código {0}).\n\nFoi guardado um registo em:\n{1}\n\nEnvia este ficheiro ao programador.", "El juego se cerró con un error (código {0}).\n\nSe guardó un registro en:\n{1}\n\nEnvía este archivo al desarrollador.", "Le jeu s'est fermé avec une erreur (code {0}).\n\nUn journal a été enregistré dans :\n{1}\n\nEnvoyez ce fichier au développeur.", "Игра закрылась с ошибкой (код {0}).\n\nЖурнал сохранён здесь:\n{1}\n\nОтправьте этот файл разработчику.", "เกมปิดลงพร้อมข้อผิดพลาด (รหัส {0})\n\nบันทึกล็อกไว้ที่:\n{1}\n\nส่งไฟล์นี้ให้ผู้พัฒนา" },
        ["starting"] = new[] { "{0} is starting…", "{0} está a iniciar…", "{0} se está iniciando…", "{0} démarre…", "{0} запускается…", "{0} กำลังเริ่มต้น…" },
        ["running"] = new[] { "{0} is running.", "{0} está em execução.", "{0} se está ejecutando.", "{0} est en cours d'exécution.", "{0} запущен.", "{0} กำลังทำงาน" },
        ["title_launch_error"] = new[] { "Launch Error", "Erro ao iniciar", "Error al iniciar", "Erreur de lancement", "Ошибка запуска", "ข้อผิดพลาดในการเปิดเกม" },
        ["launch_failed"] = new[] { "Failed to launch.", "Falha ao iniciar.", "Error al iniciar.", "Échec du lancement.", "Не удалось запустить.", "เปิดเกมไม่สำเร็จ" },
        ["res_installing"] = new[] { "Installing Frost Client version files…", "A instalar os ficheiros da versão do Frost Client…", "Instalando los archivos de la versión de Frost Client…", "Installation des fichiers de la version Frost Client…", "Установка файлов версии Frost Client…", "กำลังติดตั้งไฟล์เวอร์ชัน Frost Client…" },
        ["res_reading"] = new[] { "Reading Frost Client dependencies…", "A ler as dependências do Frost Client…", "Leyendo las dependencias de Frost Client…", "Lecture des dépendances de Frost Client…", "Чтение зависимостей Frost Client…", "กำลังอ่านไลบรารีที่ต้องใช้ของ Frost Client…" },
        ["res_downloading_libs"] = new[] { "Downloading {0} missing libraries…", "A descarregar {0} bibliotecas em falta…", "Descargando {0} bibliotecas que faltan…", "Téléchargement de {0} bibliothèques manquantes…", "Загрузка недостающих библиотек: {0}…", "กำลังดาวน์โหลดไลบรารีที่ขาดหาย {0} รายการ…" },
        ["res_libs_progress"] = new[] { "Downloading libraries… {0}/{1}", "A descarregar bibliotecas… {0}/{1}", "Descargando bibliotecas… {0}/{1}", "Téléchargement des bibliothèques… {0}/{1}", "Загрузка библиотек… {0}/{1}", "กำลังดาวน์โหลดไลบรารี… {0}/{1}" },
        ["res_done"] = new[] { "Frost Client installed!", "Frost Client instalado!", "¡Frost Client instalado!", "Frost Client installé !", "Frost Client установлен!", "ติดตั้ง Frost Client แล้ว!" },
    };
}
