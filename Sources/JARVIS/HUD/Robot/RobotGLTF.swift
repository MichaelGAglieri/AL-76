// A minimal glTF 2.0 loader for SceneKit: separate .gltf + .bin, triangle meshes, node matrices or TRS,
// metallic-roughness materials with textures (downscaled on load). Enough for the companion model.
import AppKit
import SceneKit
import ImageIO

enum RobotGLTF {
    struct Failure: Error { let message: String }

    static func load(_ url: URL, maxTexture: Int = 1024) throws -> (root: SCNNode, named: [String: SCNNode]) {
        let json = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
        let dir = url.deletingLastPathComponent()
        let buffers = try (json["buffers"] as! [[String: Any]]).map { try Data(contentsOf: dir.appendingPathComponent($0["uri"] as! String)) }
        let views = json["bufferViews"] as! [[String: Any]]
        let accessors = json["accessors"] as! [[String: Any]]
        let images = (json["images"] as? [[String: Any]] ?? []).map { dir.appendingPathComponent($0["uri"] as! String) }
        let textures = json["textures"] as? [[String: Any]] ?? []
        var imageCache: [Int: CGImage] = [:]

        func image(_ textureIndex: Int) -> CGImage? {
            guard let source = textures[textureIndex]["source"] as? Int else { return nil }
            if let cached = imageCache[source] { return cached }
            guard let src = CGImageSourceCreateWithURL(images[source] as CFURL, nil),
                  let img = CGImageSourceCreateThumbnailAtIndex(src, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true,
                                                                         kCGImageSourceThumbnailMaxPixelSize: maxTexture] as CFDictionary)
            else { return nil }
            imageCache[source] = img
            return img
        }

        func bytes(_ accessor: [String: Any]) -> (Data, Int, Int) {
            let view = views[accessor["bufferView"] as! Int]
            let offset = (view["byteOffset"] as? Int ?? 0) + (accessor["byteOffset"] as? Int ?? 0)
            return (buffers[view["buffer"] as! Int], offset, view["byteStride"] as? Int ?? 0)
        }

        func source(_ index: Int, _ semantic: SCNGeometrySource.Semantic) -> SCNGeometrySource {
            let a = accessors[index]
            let comps = ["SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4][a["type"] as! String]!
            let (data, offset, stride) = bytes(a)
            let count = a["count"] as! Int
            let s = stride == 0 ? comps * 4 : stride
            return SCNGeometrySource(data: data.subdata(in: offset..<offset + s * (count - 1) + comps * 4), semantic: semantic,
                                     vectorCount: count, usesFloatComponents: true, componentsPerVector: comps,
                                     bytesPerComponent: 4, dataOffset: 0, dataStride: s)
        }

        func element(_ index: Int) -> SCNGeometryElement {
            let a = accessors[index]
            let (data, offset, _) = bytes(a)
            let count = a["count"] as! Int
            let size = [5121: 1, 5123: 2, 5125: 4][a["componentType"] as! Int]!
            var d = data.subdata(in: offset..<offset + count * size)
            if size == 1 { d = Data(d.flatMap { [$0, 0] }) }
            return SCNGeometryElement(data: d, primitiveType: .triangles, primitiveCount: count / 3, bytesPerIndex: max(2, size))
        }

        let materials: [SCNMaterial] = (json["materials"] as? [[String: Any]] ?? []).map { m in
            let mat = SCNMaterial()
            mat.name = m["name"] as? String
            mat.lightingModel = .physicallyBased
            let pbr = m["pbrMetallicRoughness"] as? [String: Any] ?? [:]
            if let t = (pbr["baseColorTexture"] as? [String: Any])?["index"] as? Int { mat.diffuse.contents = image(t) }
            else if let c = pbr["baseColorFactor"] as? [Double] { mat.diffuse.contents = NSColor(srgbRed: c[0], green: c[1], blue: c[2], alpha: c[3]) }
            if let t = (pbr["metallicRoughnessTexture"] as? [String: Any])?["index"] as? Int, let img = image(t) {
                mat.roughness.contents = img
                mat.roughness.textureComponents = .green
                mat.metalness.contents = img
                mat.metalness.textureComponents = .blue
            } else {
                mat.roughness.contents = pbr["roughnessFactor"] as? Double ?? 1
                mat.metalness.contents = pbr["metallicFactor"] as? Double ?? 1
            }
            if let t = (m["normalTexture"] as? [String: Any])?["index"] as? Int { mat.normal.contents = image(t) }
            if let t = (m["emissiveTexture"] as? [String: Any])?["index"] as? Int { mat.emission.contents = image(t) }
            mat.isDoubleSided = m["doubleSided"] as? Bool ?? false
            for p in [mat.diffuse, mat.roughness, mat.metalness, mat.normal, mat.emission] {
                p.wrapS = .repeat; p.wrapT = .repeat; p.mipFilter = .linear
            }
            return mat
        }

        let meshes: [SCNGeometry] = (json["meshes"] as! [[String: Any]]).map { mesh in
            var sources: [SCNGeometrySource] = [], elements: [SCNGeometryElement] = [], mats: [SCNMaterial] = []
            for p in mesh["primitives"] as! [[String: Any]] {
                // One geometry per primitive would be cleaner; the model has one primitive per mesh.
                let attrs = p["attributes"] as! [String: Int]
                sources.append(source(attrs["POSITION"]!, .vertex))
                if let n = attrs["NORMAL"] { sources.append(source(n, .normal)) }
                if let t = attrs["TEXCOORD_0"] { sources.append(source(t, .texcoord)) }
                if let t = attrs["TANGENT"] { sources.append(source(t, .tangent)) }
                elements.append(element(p["indices"] as! Int))
                if let m = p["material"] as? Int { mats.append(materials[m]) }
            }
            let g = SCNGeometry(sources: sources, elements: elements)
            g.materials = mats
            return g
        }

        let nodes = json["nodes"] as! [[String: Any]]
        var named: [String: SCNNode] = [:]
        func build(_ i: Int) -> SCNNode {
            let n = nodes[i]
            let node = SCNNode()
            node.name = n["name"] as? String
            if let m = n["matrix"] as? [Double] {
                let f = m.map { CGFloat($0) }
                node.transform = SCNMatrix4(m11: f[0], m12: f[1], m13: f[2], m14: f[3], m21: f[4], m22: f[5], m23: f[6], m24: f[7],
                                            m31: f[8], m32: f[9], m33: f[10], m34: f[11], m41: f[12], m42: f[13], m43: f[14], m44: f[15])
            } else {
                if let t = n["translation"] as? [Double] { node.position = SCNVector3(t[0], t[1], t[2]) }
                if let r = n["rotation"] as? [Double] { node.orientation = SCNQuaternion(r[0], r[1], r[2], r[3]) }
                if let s = n["scale"] as? [Double] { node.scale = SCNVector3(s[0], s[1], s[2]) }
            }
            if let m = n["mesh"] as? Int { node.geometry = meshes[m].copy() as? SCNGeometry }
            for c in n["children"] as? [Int] ?? [] { node.addChildNode(build(c)) }
            if let name = node.name { named[name] = node }
            return node
        }
        let scene = (json["scenes"] as! [[String: Any]])[json["scene"] as? Int ?? 0]
        let root = SCNNode()
        for i in scene["nodes"] as! [Int] { root.addChildNode(build(i)) }
        return (root, named)
    }
}
