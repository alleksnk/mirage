#if defined(__APPLE__)

#import <Cocoa/Cocoa.h>
#import <WebKit/WebKit.h>

extern void mirageOnWillTerminate(void);
extern int mirageGetEngineStatus(void);
extern void mirageToggleEngine(void);

@interface MirageAppDelegate : NSObject <NSApplicationDelegate, NSMenuDelegate>
@property (nonatomic, strong) NSWindow *window;
@property (nonatomic, strong) WKWebView *webView;
@property (nonatomic, copy) NSString *initialURL;
@property (nonatomic, strong) NSStatusItem *statusItem;
@property (nonatomic, strong) NSMenuItem *statusMenuItem;
@property (nonatomic, strong) NSMenuItem *toggleMenuItem;
@property (nonatomic, strong) NSTimer *statusTimer;
@property (nonatomic, assign) int lastStatus;
@end

@implementation MirageAppDelegate

- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    [self setupMenuBar];
    [self setupStatusBarItem];

    NSRect frame = NSMakeRect(0, 0, 580, 720);
    NSWindowStyleMask style = NSWindowStyleMaskTitled |
                              NSWindowStyleMaskClosable |
                              NSWindowStyleMaskMiniaturizable |
                              NSWindowStyleMaskResizable;

    self.window = [[NSWindow alloc] initWithContentRect:frame
                                              styleMask:style
                                                backing:NSBackingStoreBuffered
                                                  defer:NO];
    [self.window setTitle:@"Mirage"];
    [self.window setMinSize:NSMakeSize(460, 560)];
    [self.window setReleasedWhenClosed:NO];
    [self.window center];

    WKWebViewConfiguration *config = [[WKWebViewConfiguration alloc] init];
    self.webView = [[WKWebView alloc] initWithFrame:[[self.window contentView] bounds]
                                      configuration:config];
    [self.webView setAutoresizingMask:NSViewWidthSizable | NSViewHeightSizable];
    [[self.window contentView] addSubview:self.webView];

    if (self.initialURL != nil) {
        NSURL *url = [NSURL URLWithString:self.initialURL];
        if (url != nil) {
            [self.webView loadRequest:[NSURLRequest requestWithURL:url]];
        }
    }

    [self.window makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}

- (BOOL)applicationShouldHandleReopen:(NSApplication *)sender hasVisibleWindows:(BOOL)flag {
    [self showMainWindow:nil];
    return YES;
}

- (void)applicationWillTerminate:(NSNotification *)notification {
    if (self.statusTimer != nil) {
        [self.statusTimer invalidate];
        self.statusTimer = nil;
    }
    mirageOnWillTerminate();
}

- (void)setupStatusBarItem {
    self.lastStatus = -1;
    self.statusItem = [[NSStatusBar systemStatusBar] statusItemWithLength:NSVariableStatusItemLength];

    NSMenu *menu = [[NSMenu alloc] init];
    [menu setDelegate:self];

    self.statusMenuItem = [[NSMenuItem alloc] initWithTitle:@"Статус: ..." action:nil keyEquivalent:@""];
    [self.statusMenuItem setEnabled:NO];
    [menu addItem:self.statusMenuItem];

    self.toggleMenuItem = [[NSMenuItem alloc] initWithTitle:@"Переключить обход"
                                                     action:@selector(toggleBypass:)
                                              keyEquivalent:@""];
    [self.toggleMenuItem setTarget:self];
    [menu addItem:self.toggleMenuItem];

    [menu addItem:[NSMenuItem separatorItem]];

    NSMenuItem *openItem = [[NSMenuItem alloc] initWithTitle:@"Открыть окно Mirage"
                                                      action:@selector(showMainWindow:)
                                               keyEquivalent:@""];
    [openItem setTarget:self];
    [menu addItem:openItem];

    [menu addItem:[NSMenuItem separatorItem]];

    NSMenuItem *quitItem = [[NSMenuItem alloc] initWithTitle:@"Выйти из Mirage"
                                                      action:@selector(terminate:)
                                               keyEquivalent:@"q"];
    [menu addItem:quitItem];

    self.statusItem.menu = menu;

    [self updateStatusIndicator];
    self.statusTimer = [NSTimer scheduledTimerWithTimeInterval:1.0
                                                        target:self
                                                      selector:@selector(updateStatusIndicator)
                                                      userInfo:nil
                                                       repeats:YES];
}

- (void)updateStatusIndicator {
    int st = mirageGetEngineStatus();
    if (st == self.lastStatus) {
        return;
    }
    self.lastStatus = st;

    NSStatusBarButton *button = self.statusItem.button;
    if (button == nil) {
        return;
    }

    NSString *symbolName = @"shield.slash";
    NSString *fallbackTitle = @"Mirage ○";
    NSColor *tintColor = nil;
    NSString *tip = @"Mirage DPI — Выключен";

    if (st == 1) {
        symbolName = @"checkmark.shield.fill";
        fallbackTitle = @"Mirage ●";
        tintColor = [NSColor systemGreenColor];
        tip = @"Mirage DPI — Обход включён";
    } else if (st == 2) {
        symbolName = @"shield.lefthalf.filled";
        fallbackTitle = @"Mirage …";
        tintColor = [NSColor systemOrangeColor];
        tip = @"Mirage DPI — Подключение...";
    }

    NSImage *img = nil;
    if (@available(macOS 11.0, *)) {
        img = [NSImage imageWithSystemSymbolName:symbolName
                        accessibilityDescription:@"Mirage"];
    }
    if (img != nil) {
        [img setTemplate:YES];
        button.image = img;
        button.title = @"";
        button.contentTintColor = tintColor;
    } else {
        button.image = nil;
        button.title = fallbackTitle;
        button.contentTintColor = tintColor;
    }
    button.toolTip = tip;
}

- (void)menuWillOpen:(NSMenu *)menu {
    [self updateStatusIndicator];
    int st = self.lastStatus;
    if (st == 2) {
        [self.statusMenuItem setTitle:@"Статус: Подключение..."];
        [self.toggleMenuItem setTitle:@"Остановить"];
    } else if (st == 1) {
        [self.statusMenuItem setTitle:@"Статус: Обход включён"];
        [self.toggleMenuItem setTitle:@"Выключить обход"];
    } else {
        [self.statusMenuItem setTitle:@"Статус: Выключен"];
        [self.toggleMenuItem setTitle:@"Включить обход"];
    }
}

- (void)toggleBypass:(id)sender {
    mirageToggleEngine();
}

- (void)setupMenuBar {
    NSMenu *mainMenu = [[NSMenu alloc] init];

    // Application menu
    NSMenuItem *appMenuItem = [[NSMenuItem alloc] init];
    [mainMenu addItem:appMenuItem];
    NSMenu *appMenu = [[NSMenu alloc] init];
    [appMenu addItemWithTitle:@"Окно Mirage" action:@selector(showMainWindow:) keyEquivalent:@""];
    [appMenu addItem:[NSMenuItem separatorItem]];
    [appMenu addItemWithTitle:@"Скрыть Mirage" action:@selector(hide:) keyEquivalent:@"h"];
    [appMenu addItem:[NSMenuItem separatorItem]];
    [appMenu addItemWithTitle:@"Выйти из Mirage" action:@selector(terminate:) keyEquivalent:@"q"];
    [appMenuItem setSubmenu:appMenu];

    // Edit menu (enables Cmd+C/V/X/A/Z in WKWebView inputs)
    NSMenuItem *editMenuItem = [[NSMenuItem alloc] init];
    [mainMenu addItem:editMenuItem];
    NSMenu *editMenu = [[NSMenu alloc] initWithTitle:@"Правка"];
    [editMenu addItemWithTitle:@"Отменить" action:@selector(undo:) keyEquivalent:@"z"];
    [editMenu addItemWithTitle:@"Повторить" action:@selector(redo:) keyEquivalent:@"Z"];
    [editMenu addItem:[NSMenuItem separatorItem]];
    [editMenu addItemWithTitle:@"Вырезать" action:@selector(cut:) keyEquivalent:@"x"];
    [editMenu addItemWithTitle:@"Копировать" action:@selector(copy:) keyEquivalent:@"c"];
    [editMenu addItemWithTitle:@"Вставить" action:@selector(paste:) keyEquivalent:@"v"];
    [editMenu addItemWithTitle:@"Выделить всё" action:@selector(selectAll:) keyEquivalent:@"a"];
    [editMenuItem setSubmenu:editMenu];

    // Window menu
    NSMenuItem *winMenuItem = [[NSMenuItem alloc] init];
    [mainMenu addItem:winMenuItem];
    NSMenu *winMenu = [[NSMenu alloc] initWithTitle:@"Окно"];
    [winMenu addItemWithTitle:@"Свернуть" action:@selector(performMiniaturize:) keyEquivalent:@"m"];
    [winMenu addItemWithTitle:@"Закрыть окно" action:@selector(performClose:) keyEquivalent:@"w"];
    [winMenuItem setSubmenu:winMenu];

    [NSApp setMainMenu:mainMenu];
}

- (void)showMainWindow:(id)sender {
    if (self.window != nil) {
        [self.window makeKeyAndOrderFront:nil];
        [NSApp activateIgnoringOtherApps:YES];
    }
}

@end

void mirageRunApp(const char *urlCStr) {
    @autoreleasepool {
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];

        MirageAppDelegate *delegate = [[MirageAppDelegate alloc] init];
        delegate.initialURL = [NSString stringWithUTF8String:urlCStr];
        [NSApp setDelegate:delegate];

        [NSApp run];
    }
}

void mirageTerminateApp(void) {
    dispatch_async(dispatch_get_main_queue(), ^{
        [NSApp terminate:nil];
    });
}

#endif
