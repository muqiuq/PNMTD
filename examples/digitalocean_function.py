import hmac
import hashlib
import string
import random

def main(args):
    challenge = args.get("challenge")
    if challenge == None:
        return {"body": ""}


    secret = "Your UplinkCheck__SharedKey"

    random_component = get_random_string(16)

    signature = hmac.new(
    bytes(secret, 'utf-8'),
    msg=bytes(challenge + "-" + random_component, 'utf-8'),
    digestmod=hashlib.sha256).hexdigest().upper()

    return {"body": signature + "," + random_component}

def get_random_string(length):
    # choose from all lowercase letter
    letters = string.ascii_uppercase
    result_str = ''.join(random.choice(letters) for i in range(length))
    return result_str