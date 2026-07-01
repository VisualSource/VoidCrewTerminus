import { serve } from "bun";
import * as z from "zod";
import { resolve } from "node:path";
import { readdir } from "node:fs/promises"

const contentDir = process.env.CONTENT_DIR_ROOT as string;

class HTTPDetails{
    status: number
    constructor(code: number){
        this.status = code;
    }
}

const authRequest = (req: Bun.BunRequest) => {
    const authHeader = req.headers.get("Authentication");
    if(!authHeader) throw new Error("no authentication header", { cause: new HTTPDetails(401) });
    const token = authHeader.replace("Bearer","").trim();

    if(token !== process.env.AUTH_TOKEN) throw new Error("unauthorized", { cause: new HTTPDetails(401) }); 
}

const fileQuery = z.object({
    path: z.string().max(1024).min(4).regex(/(\.cs)^/).trim().transform((arg)=>decodeURIComponent(arg))
});

const dirQuery = z.object({
    dir: z.string().min(1).trim().transform((arg)=>decodeURIComponent(arg))
})

// https://developers.cloudflare.com/tunnel/
const server = serve({
    hostname: "0.0.0.0",
    port: 3000,
    routes: {
        "/api/search": {
            GET: async (req) =>{
                authRequest(req);
                

                return Response.json();
            },
        },
        "/api/file": {
            GET: async (req)=>{
                authRequest(req);

                const url = new URL(req.url,"http://localhost");

                const result = fileQuery.safeParse(Object.fromEntries(url.searchParams.entries()))
                if(!result.success) {
                    throw new Error(z.prettifyError(result.error),{ cause: new HTTPDetails(400) })
                }
                const { path } = result.data;
                
                const filepath = resolve(contentDir,path);

                const fileContent = await Bun.file(filepath)

                //TODO: check range
        
                return new Response(fileContent);
            }
        },
        "/api/list": {
            GET: async (req)=>{
                 authRequest(req);

                const url = new URL(req.url,"http://localhost");

                const result = dirQuery.safeParse(Object.fromEntries(url.searchParams.entries()))
                if(!result.success) {
                    throw new Error(z.prettifyError(result.error),{ cause: new HTTPDetails(400) })
                }
                const { dir } = result.data;

                const filepath = resolve(contentDir,dir);

                const dirContent = await readdir(filepath,{ withFileTypes: true });

                return Response.json(
                  dirContent.map(d=>({ 
                    name: d.name,
                    isFile: d.isFile(),
                 })));
            }
        },
    },
    error(error) {
        console.error(error);
        const status =  error.cause instanceof HTTPDetails ? error.cause.status : 500;

        return Response.json({
            type: "error",
            message: Error.isError(error) ? error.message : String(error)
        },{ status })
    },
});

console.log(`api ready ${server.url}`);