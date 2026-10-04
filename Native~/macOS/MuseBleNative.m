// In-process Unity macOS BLE peripheral. Protocol/authorization stay in C#.
#import <Foundation/Foundation.h>
#import <CoreBluetooth/CoreBluetooth.h>
#import <Security/Security.h>
#import <LocalAuthentication/LocalAuthentication.h>

typedef void (*MuseEvent)(int, const char *, const char *);
@interface MusePeripheral : NSObject<CBPeripheralManagerDelegate>
@property(nonatomic, strong) CBPeripheralManager *manager;
@property(nonatomic, strong) CBMutableCharacteristic *tx;
@property(nonatomic, strong) CBCentral *central;
@property(nonatomic, strong) NSMutableArray<NSData *> *packets;
@property(nonatomic, strong) NSData *last;
@property(nonatomic, copy) NSString *name;
@property(nonatomic) int identity;
@property(atomic) MuseEvent callback;
@property(nonatomic) BOOL closed, completed, sending, finishing;
@end

@implementation MusePeripheral
- (CBUUID *)serviceID { return [CBUUID UUIDWithString:@"7fdd3d1c-38ea-46cf-8b46-314ecf5f240c"]; }
- (CBUUID *)rxID { return [CBUUID UUIDWithString:@"4d593029-28a2-4a6e-a1f0-3c2d5e8f9b01"]; }
- (CBUUID *)txID { return [CBUUID UUIDWithString:@"d75dc4ca-7b2b-4e9c-8f0a-1d2e3f4a5b6c"]; }
- (void)emit:(NSString *)kind value:(NSString *)value {
    MuseEvent callback = self.callback;
    if (!self.closed && callback) callback(self.identity, kind.UTF8String, value.UTF8String);
}
- (void)stop {
    if (self.closed) return; self.closed = YES;
    [self.manager stopAdvertising]; [self.manager removeAllServices]; self.manager.delegate = nil;
    self.manager = nil; self.central = nil; [self.packets removeAllObjects]; self.last = nil; self.callback = NULL;
}
- (void)fail:(NSString *)code { [self emit:@"error" value:code]; [self stop]; }
- (void)start:(NSString *)name {
    if (self.closed || self.manager) return;
    if (![[NSBundle mainBundle] objectForInfoDictionaryKey:@"NSBluetoothAlwaysUsageDescription"]) { [self fail:@"BLE_USAGE_DESCRIPTION_REQUIRED"]; return; }
    NSRegularExpression *regex = [NSRegularExpression regularExpressionWithPattern:@"^MuseGadget[0-9A-F]{6}$" options:0 error:nil];
    if (!name || [regex numberOfMatchesInString:name options:0 range:NSMakeRange(0, name.length)] != 1) { [self fail:@"PAIR_NAME_INVALID"]; return; }
    self.name = name; self.packets = [NSMutableArray array]; self.last = [NSData data];
    self.manager = [[CBPeripheralManager alloc] initWithDelegate:self queue:dispatch_get_main_queue() options:@{CBPeripheralManagerOptionShowPowerAlertKey:@YES}];
    __weak MusePeripheral *weakSelf = self;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 600 * NSEC_PER_SEC), dispatch_get_main_queue(), ^{
        MusePeripheral *target = weakSelf; if (target && !target.closed && !target.completed) [target fail:@"PAIR_WINDOW_EXPIRED"];
    });
}
- (void)peripheralManagerDidUpdateState:(CBPeripheralManager *)manager {
    if (self.closed) return;
    if (manager.state == CBManagerStateUnauthorized) { [self fail:@"BLUETOOTH_PERMISSION_REQUIRED"]; return; }
    if (manager.state == CBManagerStateUnsupported) { [self fail:@"BLE_PERIPHERAL_NOT_SUPPORTED"]; return; }
    if (manager.state == CBManagerStatePoweredOff) { [self fail:@"BLUETOOTH_OFF"]; return; }
    if (manager.state != CBManagerStatePoweredOn) return;
    [manager removeAllServices];
    CBMutableService *service = [[CBMutableService alloc] initWithType:self.serviceID primary:YES];
    CBMutableCharacteristic *rx = [[CBMutableCharacteristic alloc] initWithType:self.rxID properties:CBCharacteristicPropertyWrite | CBCharacteristicPropertyWriteWithoutResponse value:nil permissions:CBAttributePermissionsWriteable];
    self.tx = [[CBMutableCharacteristic alloc] initWithType:self.txID properties:CBCharacteristicPropertyRead | CBCharacteristicPropertyNotify value:nil permissions:CBAttributePermissionsReadable];
    service.characteristics = @[rx, self.tx]; [manager addService:service];
}
- (void)peripheralManager:(CBPeripheralManager *)manager didAddService:(CBService *)service error:(NSError *)error {
    if (self.closed) return;
    if (error) { [self fail:@"BLE_GATT_FAILED"]; return; }
    // Same name-only advertisement as the upstream macOS transport; service is published in GATT.
    [manager startAdvertising:@{CBAdvertisementDataLocalNameKey:self.name}];
}
- (void)peripheralManagerDidStartAdvertising:(CBPeripheralManager *)manager error:(NSError *)error {
    if (error) [self fail:@"BLE_ADVERTISE_FAILED"]; else [self emit:@"advertising" value:self.name];
}
- (BOOL)same:(CBCentral *)central { return !self.central || [self.central.identifier isEqual:central.identifier]; }
- (void)peripheralManager:(CBPeripheralManager *)manager central:(CBCentral *)central didSubscribeToCharacteristic:(CBCharacteristic *)characteristic {
    if (self.closed || ![self same:central] || ![characteristic.UUID isEqual:self.txID]) return;
    self.central = central; [self emit:@"connected" value:@""]; [self flush];
}
- (void)peripheralManager:(CBPeripheralManager *)manager central:(CBCentral *)central didUnsubscribeFromCharacteristic:(CBCharacteristic *)characteristic {
    if (self.closed || !self.central || ![self same:central]) return;
    if (self.completed) { [self emit:@"completed" value:@""]; [self stop]; }
    else [self fail:@"PAIR_PHONE_DISCONNECTED"];
}
- (void)peripheralManager:(CBPeripheralManager *)manager didReceiveWriteRequests:(NSArray<CBATTRequest *> *)requests {
    if (self.closed || requests.count == 0) return;
    CBATTRequest *first = requests.firstObject;
    for (CBATTRequest *request in requests) {
        if (self.completed || ![self same:request.central] || ![request.central.identifier isEqual:first.central.identifier] ||
            ![request.characteristic.UUID isEqual:self.rxID] || request.offset != 0 || request.value.length == 0 || request.value.length > 8192) {
            [manager respondToRequest:first withResult:CBATTErrorWriteNotPermitted]; return;
        }
    }
    self.central = first.central;
    for (CBATTRequest *request in requests) [self emit:@"write" value:[request.value base64EncodedStringWithOptions:0]];
    [manager respondToRequest:first withResult:CBATTErrorSuccess];
}
- (void)peripheralManager:(CBPeripheralManager *)manager didReceiveReadRequest:(CBATTRequest *)request {
    if (self.closed) return;
    if (![self same:request.central] || ![request.characteristic.UUID isEqual:self.txID]) { [manager respondToRequest:request withResult:CBATTErrorReadNotPermitted]; return; }
    if (request.offset > self.last.length) { [manager respondToRequest:request withResult:CBATTErrorInvalidOffset]; return; }
    request.value = [self.last subdataWithRange:NSMakeRange(request.offset, self.last.length - request.offset)];
    [manager respondToRequest:request withResult:CBATTErrorSuccess];
}
- (void)send:(NSString *)base64 {
    if (self.closed || self.completed) return;
    NSData *packet = [[NSData alloc] initWithBase64EncodedString:base64 options:0];
    if (!packet || packet.length > 20 || self.packets.count >= 2048) { [self fail:@"BLE_NOTIFY_QUEUE_FAILED"]; return; }
    [self.packets addObject:packet]; [self flush];
}
- (void)flush {
    if (self.closed || self.sending) return;
    if (self.packets.count == 0) {
        if (self.completed && !self.finishing) {
            self.finishing = YES;
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 1500 * NSEC_PER_MSEC), dispatch_get_main_queue(), ^{
                if (!self.closed) { [self emit:@"completed" value:@""]; [self stop]; }
            });
        }
        return;
    }
    if (!self.central || ![self.tx.subscribedCentrals containsObject:self.central]) return;
    if ([self.manager updateValue:self.packets.firstObject forCharacteristic:self.tx onSubscribedCentrals:@[self.central]]) {
        self.last = self.packets.firstObject; [self.packets removeObjectAtIndex:0]; self.sending = YES;
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 50 * NSEC_PER_MSEC), dispatch_get_main_queue(), ^{ self.sending = NO; [self flush]; });
    }
}
- (void)peripheralManagerIsReadyToUpdateSubscribers:(CBPeripheralManager *)manager { [self flush]; }
@end

__attribute__((visibility("default"))) void *muse_ble_create(int identity, MuseEvent callback) {
    MusePeripheral *peripheral = [MusePeripheral new]; peripheral.identity = identity; peripheral.callback = callback;
    return (__bridge_retained void *)peripheral;
}
__attribute__((visibility("default"))) void muse_ble_start(void *handle, const char *name) {
    MusePeripheral *peripheral = (__bridge MusePeripheral *)handle; NSString *value = name ? [NSString stringWithUTF8String:name] : nil;
    dispatch_async(dispatch_get_main_queue(), ^{ [peripheral start:value]; });
}
__attribute__((visibility("default"))) void muse_ble_send(void *handle, const char *packet) {
    MusePeripheral *peripheral = (__bridge MusePeripheral *)handle; NSString *value = packet ? [NSString stringWithUTF8String:packet] : nil;
    dispatch_async(dispatch_get_main_queue(), ^{ [peripheral send:value]; });
}
__attribute__((visibility("default"))) void muse_ble_complete(void *handle) {
    MusePeripheral *peripheral = (__bridge MusePeripheral *)handle;
    dispatch_async(dispatch_get_main_queue(), ^{ if (!peripheral.closed) { peripheral.completed = YES; [peripheral flush]; } });
}
__attribute__((visibility("default"))) void muse_ble_dispose(void *handle) {
    MusePeripheral *peripheral = (__bridge_transfer MusePeripheral *)handle;
    peripheral.callback = NULL; // Clear the managed function pointer before a Unity domain reload.
    dispatch_async(dispatch_get_main_queue(), ^{ [peripheral stop]; });
}
static NSDictionary *storeQuery(void) {
    return @{(__bridge id)kSecClass:(__bridge id)kSecClassGenericPassword, (__bridge id)kSecAttrService:@"Muse Unity", (__bridge id)kSecAttrAccount:@"device-v1", (__bridge id)kSecAttrSynchronizable:@NO};
}
__attribute__((visibility("default"))) int muse_store_load(char **value) {
    *value = NULL; NSMutableDictionary *query = [storeQuery() mutableCopy]; query[(__bridge id)kSecReturnData] = @YES;
    LAContext *authentication = [LAContext new]; authentication.interactionNotAllowed = YES;
    query[(__bridge id)kSecUseAuthenticationContext] = authentication;
    CFTypeRef result = NULL; OSStatus status = SecItemCopyMatching((__bridge CFDictionaryRef)query, &result);
    if (status == errSecItemNotFound) return 0;
    if (status != errSecSuccess) return (int)status;
    NSData *data = CFBridgingRelease(result); NSString *text = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
    if (!text) return -1; *value = strdup(text.UTF8String); return *value ? 0 : -1;
}
__attribute__((visibility("default"))) int muse_store_save(const char *json) {
    NSData *data = [[NSString stringWithUTF8String:json] dataUsingEncoding:NSUTF8StringEncoding];
    OSStatus status = SecItemUpdate((__bridge CFDictionaryRef)storeQuery(), (__bridge CFDictionaryRef)@{(__bridge id)kSecValueData:data});
    if (status == errSecItemNotFound) { NSMutableDictionary *item = [storeQuery() mutableCopy]; item[(__bridge id)kSecValueData] = data; status = SecItemAdd((__bridge CFDictionaryRef)item, NULL); }
    return (int)status;
}
__attribute__((visibility("default"))) int muse_store_clear(void) {
    OSStatus status = SecItemDelete((__bridge CFDictionaryRef)storeQuery()); return status == errSecItemNotFound ? 0 : (int)status;
}
__attribute__((visibility("default"))) char *muse_store_identity(void) {
    NSString *key = @"muse.unity.identity-v1"; NSString *value = [[NSUserDefaults standardUserDefaults] stringForKey:key];
    if (!value) {
        uint8_t bytes[6]; if (SecRandomCopyBytes(kSecRandomDefault, 6, bytes) != errSecSuccess) return NULL; bytes[0] = (bytes[0] & 0xfc) | 2;
        value = [NSString stringWithFormat:@"%02x:%02x:%02x:%02x:%02x:%02x", bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5]];
        [[NSUserDefaults standardUserDefaults] setObject:value forKey:key];
    }
    return strdup(value.UTF8String);
}
__attribute__((visibility("default"))) void muse_free(void *value) { if (value) { size_t count = strlen(value); volatile unsigned char *bytes = value; while (count--) *bytes++ = 0; free(value); } }
